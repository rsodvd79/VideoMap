use anyhow::{Context, Result, bail, ensure};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    fs,
    io::Write,
    path::{Path, PathBuf},
};

use crate::model::{MediaType, Project};

#[derive(Serialize, Deserialize)]
struct Document {
    format: String,
    schema_version: u32,
    project: Project,
}

pub fn atomic_write(path: &Path, bytes: &[u8]) -> Result<()> {
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    let mut temporary = tempfile::NamedTempFile::new_in(parent)?;
    temporary.write_all(bytes)?;
    temporary.as_file().sync_all()?;
    temporary.persist(path).map_err(|e| e.error)?;
    Ok(())
}

pub fn load(path: &Path) -> Result<Project> {
    let bytes = fs::read(path).context("Lettura progetto")?;
    ensure!(bytes.len() <= 32 * 1024 * 1024, "Progetto troppo grande");
    let doc: Document = serde_json::from_slice(&bytes)
        .context("Formato non valido: i progetti della versione .NET non sono compatibili")?;
    ensure!(
        doc.format == "videomap-rust" && doc.schema_version == 1,
        "Versione o formato progetto non supportato"
    );
    doc.project.validate()?;
    let mut project = doc.project;
    let parent = absolute_parent(path)?;
    for polygon in &mut project.polygons {
        if let Some(media) = &mut polygon.media
            && media.is_relative()
        {
            *media = parent.join(&*media);
        }
    }
    Ok(project)
}

fn absolute_parent(path: &Path) -> Result<PathBuf> {
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    fs::canonicalize(parent).context("Cartella di destinazione non disponibile")
}

pub fn save(project: &Project, path: &Path) -> Result<Project> {
    project.validate()?;
    let directory = absolute_parent(path)?;
    let assets = directory.join("Assets");
    fs::create_dir_all(&assets)?;
    let mut updated = project.clone();
    let mut map = HashMap::<PathBuf, PathBuf>::new();
    for polygon in &mut updated.polygons {
        let Some(source) = polygon.media.as_ref().filter(|p| p.is_file()) else {
            continue;
        };
        let source = fs::canonicalize(source)?;
        if let Some(destination) = map.get(&source) {
            polygon.media = Some(destination.clone());
            continue;
        }
        let destination = if source.starts_with(&assets) {
            source.clone()
        } else {
            let name = source.file_name().context("Nome media non valido")?;
            let mut destination = assets.join(name);
            let mut suffix = 1;
            while destination.exists() {
                let stem = source.file_stem().unwrap_or(name).to_string_lossy();
                let extension = source
                    .extension()
                    .map(|s| format!(".{}", s.to_string_lossy()))
                    .unwrap_or_default();
                destination = assets.join(format!("{stem}_{suffix}{extension}"));
                suffix += 1;
            }
            let mut temporary = tempfile::NamedTempFile::new_in(&assets)?;
            let mut input = fs::File::open(&source)?;
            std::io::copy(&mut input, &mut temporary)?;
            temporary.as_file().sync_all()?;
            temporary
                .persist_noclobber(&destination)
                .map_err(|e| e.error)?;
            destination
        };
        map.insert(source, destination.clone());
        polygon.media = Some(destination);
    }
    let mut disk = updated.clone();
    for polygon in &mut disk.polygons {
        if let Some(media) = &mut polygon.media
            && let Ok(relative) = media.strip_prefix(&directory)
        {
            *media = relative.to_path_buf();
        }
    }
    let doc = Document {
        format: "videomap-rust".into(),
        schema_version: 1,
        project: disk,
    };
    atomic_write(path, &serde_json::to_vec_pretty(&doc)?).context("Salvataggio progetto")?;
    Ok(updated)
}

pub fn export(project: &Project, path: &Path) -> Result<()> {
    project.validate()?;
    let directory = absolute_parent(path)?;
    let mut preset = project.clone();
    preset.scenes.clear();
    for polygon in &mut preset.polygons {
        if let Some(media) = &mut polygon.media
            && let Ok(relative) = media.strip_prefix(&directory)
        {
            *media = relative.to_path_buf();
        }
    }
    let doc = Document {
        format: "videomap-output-rust".into(),
        schema_version: 1,
        project: preset,
    };
    atomic_write(path, &serde_json::to_vec_pretty(&doc)?)?;
    Ok(())
}

pub fn assign(polygon: &mut crate::model::Polygon, path: PathBuf) -> Result<()> {
    let kind = MediaType::from_path(&path);
    ensure!(kind != MediaType::None, "Formato media non supportato");
    ensure!(path.is_file(), "File media non disponibile");
    polygon.media = Some(path);
    polygon.media_type = kind;
    Ok(())
}

pub struct RelinkScan {
    pub files: HashMap<std::ffi::OsString, Vec<PathBuf>>,
    pub warnings: Vec<String>,
}

pub fn scan(root: &Path) -> Result<RelinkScan> {
    if !root.is_dir() {
        bail!("Cartella media non disponibile");
    }
    let mut result = RelinkScan {
        files: HashMap::new(),
        warnings: Vec::new(),
    };
    let mut pending = vec![root.to_path_buf()];
    while let Some(directory) = pending.pop() {
        let entries = match fs::read_dir(&directory) {
            Ok(entries) => entries,
            Err(error) => {
                result
                    .warnings
                    .push(format!("{}: {error}", directory.display()));
                continue;
            }
        };
        for entry in entries {
            let entry = match entry {
                Ok(entry) => entry,
                Err(error) => {
                    result.warnings.push(error.to_string());
                    continue;
                }
            };
            let file_type = entry.file_type()?;
            if file_type.is_dir() {
                pending.push(entry.path());
            } else if file_type.is_file() {
                result
                    .files
                    .entry(entry.file_name())
                    .or_default()
                    .push(entry.path());
            }
        }
    }
    for paths in result.files.values_mut() {
        paths.sort();
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::Polygon;
    #[test]
    fn save_consolidates_duplicate_sources_and_preserves_relative_paths() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let source = temp.path().join("source.png");
        fs::write(&source, b"fixture")?;
        let folder = temp.path().join("project");
        fs::create_dir(&folder)?;
        let mut project = Project::default();
        let mut p = Polygon::square("Uno".into(), project.size);
        assign(&mut p, source)?;
        project.polygons.push(p.clone());
        p.id = uuid::Uuid::new_v4();
        project.polygons.push(p);
        let path = folder.join("demo.vmap");
        save(&project, &path)?;
        let moved = temp.path().join("moved");
        fs::rename(&folder, &moved)?;
        let loaded = load(&moved.join("demo.vmap"))?;
        let moved = fs::canonicalize(moved)?;
        assert_eq!(loaded.polygons[0].media, loaded.polygons[1].media);
        assert!(
            loaded.polygons[0]
                .media
                .as_ref()
                .is_some_and(|p| p.is_file() && p.starts_with(&moved))
        );
        Ok(())
    }
    #[test]
    fn save_failure_does_not_mutate_original_or_previous_document() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let path = temp.path().join("demo.vmap");
        let mut p = Project::default();
        save(&p, &path)?;
        let previous = fs::read(&path)?;
        p.size[0] = f64::NAN;
        assert!(save(&p, &path).is_err());
        assert_eq!(fs::read(path)?, previous);
        Ok(())
    }
    #[test]
    fn distinct_sources_with_same_name_remain_distinct_and_relink_ambiguous() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let sources = temp.path().join("sources");
        let destination = temp.path().join("project");
        fs::create_dir(&destination)?;
        let mut project = Project::default();
        for (folder, contents) in [("one", b"first".as_slice()), ("two", b"second".as_slice())] {
            let folder = sources.join(folder);
            fs::create_dir_all(&folder)?;
            let path = folder.join("clip.png");
            fs::write(&path, contents)?;
            let mut polygon = Polygon::square(folder.display().to_string(), project.size);
            assign(&mut polygon, path)?;
            project.polygons.push(polygon);
        }
        let scan = scan(&sources)?;
        assert_eq!(scan.files[std::ffi::OsStr::new("clip.png")].len(), 2);
        let saved = save(&project, &destination.join("demo.vmap"))?;
        assert_ne!(saved.polygons[0].media, saved.polygons[1].media);
        assert_eq!(
            fs::read(saved.polygons[0].media.as_ref().unwrap())?,
            b"first"
        );
        assert_eq!(
            fs::read(saved.polygons[1].media.as_ref().unwrap())?,
            b"second"
        );
        Ok(())
    }
    #[test]
    fn rejects_legacy_files_without_replacing_current_project() -> Result<()> {
        let temp = tempfile::tempdir()?;
        let path = temp.path().join("legacy.vmap");
        fs::write(&path, br#"{"name":"old","polygons":[]}"#)?;
        assert!(load(&path).is_err());
        Ok(())
    }
}
