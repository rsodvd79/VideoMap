#![cfg_attr(
    all(target_os = "windows", not(debug_assertions)),
    windows_subsystem = "windows"
)]

mod app;
#[cfg(target_os = "macos")]
mod dock;
mod geometry;
mod model;
mod outputs;
mod project_io;
mod render;
mod settings;
mod ui;
mod video;

use anyhow::{Context, Result};
use std::{
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
};

fn main() -> Result<()> {
    let settings = settings::Settings::load();
    if let Some(mut child) = video::relaunch(settings.vlc_path.as_deref(), false, None)? {
        let status = child.wait()?;
        anyhow::ensure!(
            status.success(),
            "Il processo VideoMap e terminato con un errore"
        );
        return Ok(());
    }
    let mut path = None;
    let mut smoke = false;
    for argument in std::env::args_os().skip(1) {
        if argument == "--smoke-test" {
            smoke = true;
        } else {
            path = Some(PathBuf::from(argument));
        }
    }
    if smoke {
        let project = project_io::load(
            path.as_deref()
                .context("Uso: videomap --smoke-test progetto.vmap")?,
        )?;
        anyhow::ensure!(
            project.outputs.len() >= 2
                && project
                    .polygons
                    .iter()
                    .any(|p| p.media_type == model::MediaType::Video),
            "La prova richiede un video e almeno due output"
        );
    }
    let icon = image::load_from_memory(include_bytes!("../assets/AppIcon.png"))?.into_rgba8();
    let options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_title(format!("VideoMap Designer - {}", env!("VIDEOMAP_VERSION")))
            .with_inner_size([1440., 900.])
            .with_min_inner_size([900., 600.])
            .with_icon(eframe::egui::IconData {
                width: icon.width(),
                height: icon.height(),
                rgba: icon.into_raw(),
            }),
        renderer: eframe::Renderer::Wgpu,
        ..Default::default()
    };
    let verified = Arc::new(AtomicBool::new(!smoke));
    let completed = verified.clone();
    eframe::run_native(
        "VideoMap",
        options,
        Box::new(move |cc| {
            #[cfg(target_os = "macos")]
            dock::apply();
            let mut app = app::App::new(cc, settings, path.as_deref(), smoke)?;
            app.smoke_success = completed;
            Ok(Box::new(app))
        }),
    )
    .map_err(|e| anyhow::anyhow!("{e}"))?;
    anyhow::ensure!(
        verified.load(Ordering::Acquire),
        "Prova nativa non superata: verificare i frame video e le uscite"
    );
    Ok(())
}
