use anyhow::{Result, ensure};
use serde::{Deserialize, Serialize};
use std::{collections::HashSet, path::PathBuf};
use uuid::Uuid;

use crate::geometry::{Point, valid_polygon};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MediaType {
    #[default]
    None,
    Image,
    Video,
}

impl MediaType {
    pub fn from_path(path: &std::path::Path) -> Self {
        match path
            .extension()
            .and_then(|s| s.to_str())
            .unwrap_or("")
            .to_ascii_lowercase()
            .as_str()
        {
            "png" | "jpg" | "jpeg" | "bmp" | "gif" | "tiff" | "webp" => Self::Image,
            "mp4" | "mov" | "mkv" | "avi" | "webm" | "mpeg" | "mpg" => Self::Video,
            _ => Self::None,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Polygon {
    pub id: Uuid,
    pub name: String,
    pub points: Vec<Point>,
    pub media: Option<PathBuf>,
    pub media_type: MediaType,
    pub solo: bool,
    pub muted: bool,
    pub looping: bool,
}

impl Polygon {
    pub fn set_points(&mut self, points: Vec<Point>) -> bool {
        if !valid_polygon(&points) {
            return false;
        }
        self.points = points;
        true
    }

    pub fn square(name: String, surface: [f64; 2]) -> Self {
        let [w, h] = surface;
        let half = w.min(h) / 8.0;
        Self {
            id: Uuid::new_v4(),
            name,
            points: vec![
                [w / 2.0 - half, h / 2.0 - half],
                [w / 2.0 + half, h / 2.0 - half],
                [w / 2.0 + half, h / 2.0 + half],
                [w / 2.0 - half, h / 2.0 + half],
            ],
            media: None,
            media_type: MediaType::None,
            solo: false,
            muted: false,
            looping: false,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Scene {
    pub id: Uuid,
    pub name: String,
    pub duration: f64,
    pub polygons: Vec<Uuid>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct MonitorPreference {
    pub name: String,
    pub size: [u32; 2],
    pub position: [i32; 2],
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Output {
    pub id: Uuid,
    pub name: String,
    pub size: [f64; 2],
    pub polygons: Vec<Uuid>,
    pub monitor: Option<MonitorPreference>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Project {
    pub name: String,
    pub size: [f64; 2],
    pub polygons: Vec<Polygon>,
    pub scenes: Vec<Scene>,
    pub outputs: Vec<Output>,
}

impl Default for Project {
    fn default() -> Self {
        Self {
            name: "Senza nome".into(),
            size: [1920.0, 1080.0],
            polygons: Vec::new(),
            scenes: vec![Scene {
                id: Uuid::new_v4(),
                name: "Scena 1".into(),
                duration: 5.0,
                polygons: vec![],
            }],
            outputs: vec![Output {
                id: Uuid::new_v4(),
                name: "Output 1".into(),
                size: [1920.0, 1080.0],
                polygons: vec![],
                monitor: None,
            }],
        }
    }
}

impl Project {
    pub fn validate(&self) -> Result<()> {
        fn size_ok(size: [f64; 2]) -> bool {
            size.into_iter()
                .all(|v| v.is_finite() && v > 0.0 && v <= 65536.0)
        }
        ensure!(
            size_ok(self.size),
            "Dimensioni progetto non valide (1-65536 pixel)"
        );
        let mut ids = HashSet::new();
        for polygon in &self.polygons {
            ensure!(
                !polygon.id.is_nil() && ids.insert(polygon.id),
                "Identificativo poligono duplicato o nullo"
            );
            ensure!(
                valid_polygon(&polygon.points),
                "Geometria non valida: {}",
                polygon.name
            );
            ensure!(
                polygon.media_type == MediaType::None || polygon.media.is_some(),
                "Riferimento media assente: {}",
                polygon.name
            );
        }
        let polygon_ids = ids.clone();
        for scene in &self.scenes {
            ensure!(
                !scene.id.is_nil() && ids.insert(scene.id),
                "Identificativo scena duplicato o nullo"
            );
            ensure!(
                scene.duration.is_finite() && scene.duration > 0.0,
                "Durata scena non valida"
            );
            ensure!(
                scene.polygons.iter().all(|id| polygon_ids.contains(id)),
                "Poligono scena inesistente"
            );
        }
        for output in &self.outputs {
            ensure!(
                !output.id.is_nil() && ids.insert(output.id),
                "Identificativo output duplicato o nullo"
            );
            ensure!(
                size_ok(output.size),
                "Dimensioni output non valide (1-65536 pixel)"
            );
            ensure!(
                output.polygons.iter().all(|id| polygon_ids.contains(id)),
                "Poligono output inesistente"
            );
        }
        Ok(())
    }

    pub fn scene_visible(&self, scene: Option<Uuid>, polygon: Uuid) -> bool {
        self.scenes
            .iter()
            .find(|s| Some(s.id) == scene)
            .is_none_or(|s| s.polygons.is_empty() || s.polygons.contains(&polygon))
    }

    pub fn remove_polygon(&mut self, id: Uuid) {
        self.polygons.retain(|p| p.id != id);
        for scene in &mut self.scenes {
            scene.polygons.retain(|p| *p != id);
        }
        for output in &mut self.outputs {
            output.polygons.retain(|p| *p != id);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn invalid_edit_keeps_last_valid_geometry() {
        let mut polygon = Polygon::square("Test".into(), [1920., 1080.]);
        let mut valid = polygon.points.clone();
        valid[0][0] += 20.;
        assert!(polygon.set_points(valid.clone()));
        let mut crossed = valid.clone();
        crossed.swap(1, 2);
        assert!(!polygon.set_points(crossed));
        assert_eq!(polygon.points, valid);
        assert!(!polygon.set_points(vec![[0., 0.]; 4]));
        assert_eq!(polygon.points, valid);
    }
}
