use anyhow::Result;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Default, Serialize, Deserialize)]
pub struct Settings {
    pub vlc_path: Option<PathBuf>,
}

impl Settings {
    fn path() -> Result<PathBuf> {
        let dirs = directories::ProjectDirs::from("com", "VideoMap", "VideoMap")
            .ok_or_else(|| anyhow::anyhow!("Directory impostazioni non disponibile"))?;
        Ok(dirs.config_dir().join("rust-settings.json"))
    }
    pub fn load() -> Self {
        Self::path()
            .ok()
            .and_then(|p| std::fs::read(p).ok())
            .and_then(|data| serde_json::from_slice(&data).ok())
            .unwrap_or_default()
    }
    pub fn save(&self) -> Result<()> {
        let path = Self::path()?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        crate::project_io::atomic_write(&path, &serde_json::to_vec_pretty(self)?)?;
        Ok(())
    }
}
