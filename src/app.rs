use crate::{
    model::{MediaType, Project},
    outputs::Outputs,
    project_io::{self, RelinkScan},
    render::{Draw, Renderer},
    settings::Settings,
    video::{Engine, Player, Transport},
};
use anyhow::{Context, Result};
use eframe::egui;
use std::{
    collections::HashMap,
    path::{Path, PathBuf},
    sync::{Arc, mpsc},
    time::{Duration, Instant},
};
use uuid::Uuid;

pub enum Outcome {
    Loaded(Project, PathBuf),
    Saved(Project, PathBuf),
    Imported(Uuid, PathBuf),
    Scanned(RelinkScan),
    VlcPath(PathBuf),
    Message(String),
}

pub struct Media {
    path: PathBuf,
    kind: MediaType,
    pub player: Option<Player>,
    muted: bool,
    looping: bool,
    visible: bool,
    pub error: Option<String>,
}

pub struct Drag {
    pub id: Uuid,
    pub vertex: Option<usize>,
    pub start: [f64; 2],
    pub original: Vec<[f64; 2]>,
}

#[derive(Clone, Copy)]
pub enum Action {
    New,
    Open,
    Quit,
}

pub struct Timeline {
    pub index: usize,
    pub started: Instant,
}
type ImageResult = (Uuid, PathBuf, Result<image::RgbaImage>);

pub struct App {
    pub project: Project,
    pub path: Option<PathBuf>,
    pub selected: Option<Uuid>,
    pub selected_scene: Option<Uuid>,
    pub active_scene: Option<Uuid>,
    pub selected_output: Option<Uuid>,
    pub tab: crate::ui::SidebarTab,
    pub show_settings: bool,
    pub status: String,
    pub dirty: bool,
    pub pending_action: Option<Action>,
    pub allow_close: bool,
    pub drag: Option<Drag>,
    pub settings: Settings,
    pub vlc_edit: String,
    pub vlc_status: String,
    pub engine: Option<Arc<Engine>>,
    pub renderer: Renderer,
    pub media: HashMap<Uuid, Media>,
    pub outputs: Outputs,
    pub transport: Transport,
    pub seek: f32,
    pub timeline: Option<Timeline>,
    pub scene_progress: f32,
    pub job: Option<mpsc::Receiver<Result<Option<Outcome>>>>,
    pub ambiguities: Vec<(Uuid, Vec<PathBuf>)>,
    image_jobs: mpsc::Sender<(Uuid, PathBuf)>,
    image_results: mpsc::Receiver<ImageResult>,
    pub smoke: Option<Instant>,
    pub smoke_frames: u64,
    pub smoke_success: Arc<std::sync::atomic::AtomicBool>,
    smoke_phase: u8,
    smoke_hidden_frames: u64,
    smoke_progressed: bool,
    smoke_paused: bool,
    smoke_stopped: bool,
}

impl App {
    pub fn new(
        cc: &eframe::CreationContext<'_>,
        settings: Settings,
        project_path: Option<&Path>,
        smoke: bool,
    ) -> Result<Self> {
        crate::ui::configure_style(&cc.egui_ctx);
        let renderer = Renderer::new(
            cc.wgpu_render_state
                .as_ref()
                .context("Backend GPU wgpu non disponibile")?,
        );
        let engine = Engine::load(settings.vlc_path.as_deref());
        let vlc_status = match &engine {
            Ok(engine) => format!("LibVLC {}", engine.version),
            Err(e) => format!("{e:#}"),
        };
        let vlc_edit = settings
            .vlc_path
            .as_ref()
            .map(|p| p.to_string_lossy().into_owned())
            .unwrap_or_default();
        let mut status = "Progetto non salvato".to_owned();
        let mut path = None;
        let project = if let Some(project_path) = project_path {
            match project_io::load(project_path) {
                Ok(project) => {
                    path = Some(project_path.to_path_buf());
                    status = format!(
                        "Progetto caricato: {}",
                        project_path
                            .file_name()
                            .unwrap_or_default()
                            .to_string_lossy()
                    );
                    project
                }
                Err(e) => {
                    status = format!("{e:#}");
                    Project::default()
                }
            }
        } else {
            Project::default()
        };
        let (image_jobs, rx) = mpsc::channel::<(Uuid, PathBuf)>();
        let (tx, image_results) = mpsc::sync_channel(2);
        std::thread::Builder::new()
            .name("videomap-images".into())
            .spawn(move || {
                while let Ok((id, path)) = rx.recv() {
                    let image = (|| {
                        let mut reader = image::ImageReader::open(&path)?.with_guessed_format()?;
                        let mut limits = image::Limits::default();
                        limits.max_alloc = Some(256 * 1024 * 1024);
                        limits.max_image_width = Some(16384);
                        limits.max_image_height = Some(16384);
                        reader.limits(limits);
                        Ok(reader.decode()?.into_rgba8())
                    })();
                    if tx.send((id, path, image)).is_err() {
                        break;
                    }
                }
            })?;
        let scene = project.scenes.first().map(|s| s.id);
        let output = project.outputs.first().map(|o| o.id);
        Ok(Self {
            project,
            path,
            selected: None,
            selected_scene: scene,
            active_scene: scene,
            selected_output: output,
            tab: crate::ui::SidebarTab::Layers,
            show_settings: false,
            status,
            dirty: false,
            pending_action: None,
            allow_close: false,
            drag: None,
            settings,
            vlc_edit,
            vlc_status,
            engine: engine.ok(),
            renderer,
            media: HashMap::new(),
            outputs: Outputs::default(),
            transport: Transport::Play,
            seek: 0.,
            timeline: None,
            scene_progress: 0.,
            job: None,
            ambiguities: vec![],
            image_jobs,
            image_results,
            smoke: smoke.then(Instant::now),
            smoke_frames: 0,
            smoke_success: Arc::new(std::sync::atomic::AtomicBool::new(false)),
            smoke_phase: 0,
            smoke_hidden_frames: 0,
            smoke_progressed: false,
            smoke_paused: false,
            smoke_stopped: false,
        })
    }

    pub fn start_job(&mut self, job: impl FnOnce() -> Result<Option<Outcome>> + Send + 'static) {
        if self.job.is_some() {
            return;
        }
        let (tx, rx) = mpsc::channel();
        self.job = Some(rx);
        std::thread::spawn(move || {
            let _ = tx.send(job());
        });
    }

    pub fn request(&mut self, action: Action) {
        if self.dirty {
            self.pending_action = Some(action);
        } else {
            self.execute(action);
        }
    }

    pub fn execute(&mut self, action: Action) {
        match action {
            Action::Quit => self.allow_close = true,
            Action::New => self.replace(Project::default(), None),
            Action::Open => self.start_job(|| {
                let Some(path) = rfd::FileDialog::new()
                    .set_title("Carica progetto Rust")
                    .add_filter("VideoMap", &["vmap"])
                    .pick_file()
                else {
                    return Ok(None);
                };
                Ok(Some(Outcome::Loaded(project_io::load(&path)?, path)))
            }),
        }
    }

    pub fn replace(&mut self, project: Project, path: Option<PathBuf>) {
        self.media.clear();
        self.renderer.clear();
        self.outputs.clear();
        self.selected = None;
        self.tab = crate::ui::SidebarTab::Layers;
        self.active_scene = project.scenes.first().map(|s| s.id);
        self.selected_scene = self.active_scene;
        self.selected_output = project.outputs.first().map(|o| o.id);
        self.project = project;
        self.path = path;
        self.timeline = None;
        self.drag = None;
        self.ambiguities.clear();
        self.dirty = false;
        self.status = "Progetto pronto".into();
    }

    pub fn set_transport(&mut self, transport: Transport) {
        self.transport = transport;
        for media in self.media.values() {
            if let Some(player) = &media.player {
                player.transport(transport);
            }
        }
        if transport == Transport::Stop {
            self.seek = 0.;
        }
    }

    pub fn seek_all(&self, position: f32) {
        for media in self.media.values().filter(|m| m.visible) {
            if let Some(player) = &media.player {
                player.seek(position);
            }
        }
    }

    fn capture_smoke(ctx: &egui::Context, suffix: &str) {
        let Some(path) = std::env::var_os("VIDEOMAP_SMOKE_CAPTURE") else {
            return;
        };
        let mut path = PathBuf::from(path);
        if !suffix.is_empty() {
            let stem = path.file_stem().unwrap_or_default().to_string_lossy();
            path.set_file_name(format!("{stem}-{suffix}.png"));
        }
        ctx.request_screenshot(move |capture| {
            let pixels: Vec<_> = capture.pixels.iter().flat_map(|p| p.to_array()).collect();
            if let Err(error) = image::save_buffer(
                path,
                &pixels,
                capture.size[0] as u32,
                capture.size[1] as u32,
                image::ColorType::Rgba8,
            ) {
                eprintln!("Screenshot verifica: {error}");
            }
        });
    }

    fn receive_job(&mut self) {
        let result = self.job.as_ref().and_then(|rx| match rx.try_recv() {
            Ok(value) => Some(value),
            Err(mpsc::TryRecvError::Disconnected) => {
                Some(Err(anyhow::anyhow!("Operazione file interrotta")))
            }
            _ => None,
        });
        let Some(result) = result else {
            return;
        };
        self.job = None;
        match result {
            Err(error) => self.status = format!("Errore: {error:#}"),
            Ok(None) => {}
            Ok(Some(Outcome::Loaded(project, path))) => self.replace(project, Some(path)),
            Ok(Some(Outcome::Saved(project, path))) => {
                for polygon in &project.polygons {
                    if let (Some(media), Some(path)) =
                        (self.media.get_mut(&polygon.id), &polygon.media)
                        && media.player.is_some()
                    {
                        media.path = path.clone();
                    }
                }
                self.project = project;
                self.path = Some(path);
                self.dirty = false;
                self.status = "Progetto salvato".into();
            }
            Ok(Some(Outcome::Imported(id, path))) => {
                if let Some(polygon) = self.project.polygons.iter_mut().find(|p| p.id == id) {
                    match project_io::assign(polygon, path) {
                        Ok(()) => {
                            self.media.remove(&id);
                            self.renderer.remove(id);
                            self.dirty = true;
                            self.status = "Media associato".into();
                        }
                        Err(error) => self.status = format!("{error:#}"),
                    }
                }
            }
            Ok(Some(Outcome::Scanned(scan))) => {
                let mut count = 0;
                for polygon in &mut self.project.polygons {
                    let Some(path) = polygon.media.as_ref().filter(|p| !p.is_file()) else {
                        continue;
                    };
                    let Some(paths) = path.file_name().and_then(|name| scan.files.get(name)) else {
                        continue;
                    };
                    if paths.len() == 1 {
                        polygon.media = Some(paths[0].clone());
                        self.dirty = true;
                        count += 1;
                    } else {
                        self.ambiguities.push((polygon.id, paths.clone()));
                    }
                }
                self.status = format!(
                    "Ricollegati: {count}; ambigui: {}; cartelle non leggibili: {}",
                    self.ambiguities.len(),
                    scan.warnings.len()
                );
            }
            Ok(Some(Outcome::VlcPath(path))) => self.vlc_edit = path.to_string_lossy().into_owned(),
            Ok(Some(Outcome::Message(message))) => self.status = message,
        }
    }

    fn sync_media(&mut self) {
        let removed: Vec<_> = self
            .media
            .keys()
            .filter(|id| !self.project.polygons.iter().any(|p| p.id == **id))
            .copied()
            .collect();
        for id in removed {
            self.media.remove(&id);
            self.renderer.remove(id);
        }
        let solo = self
            .project
            .polygons
            .iter()
            .any(|p| p.solo && p.media_type == MediaType::Video);
        for polygon in &self.project.polygons {
            let visible = self.project.scene_visible(self.active_scene, polygon.id)
                && (!solo || polygon.solo || polygon.media_type != MediaType::Video);
            let Some(path) = &polygon.media else {
                self.media.remove(&polygon.id);
                self.renderer.remove(polygon.id);
                continue;
            };
            if self
                .media
                .get(&polygon.id)
                .is_none_or(|m| m.path != *path || m.kind != polygon.media_type)
            {
                self.renderer.remove(polygon.id);
                let mut media = Media {
                    path: path.clone(),
                    kind: polygon.media_type,
                    player: None,
                    muted: polygon.muted,
                    looping: polygon.looping,
                    visible,
                    error: None,
                };
                if !path.is_file() {
                    media.error = Some("Media mancante: usa Ricollega".into());
                } else if polygon.media_type == MediaType::Video {
                    if let Some(engine) = &self.engine {
                        match Player::new(engine.clone(), path.clone(), polygon.looping) {
                            Ok(player) => {
                                player.mute(polygon.muted);
                                player.visible(visible);
                                player.transport(self.transport);
                                media.player = Some(player);
                            }
                            Err(e) => media.error = Some(format!("{e:#}")),
                        }
                    } else {
                        media.error = Some(self.vlc_status.clone());
                    }
                } else if polygon.media_type == MediaType::Image {
                    let _ = self.image_jobs.send((polygon.id, path.clone()));
                }
                self.media.insert(polygon.id, media);
            }
            if let Some(media) = self.media.get_mut(&polygon.id)
                && let Some(player) = &media.player
            {
                if media.visible != visible {
                    player.visible(visible);
                    media.visible = visible;
                }
                if media.muted != polygon.muted {
                    player.mute(polygon.muted);
                    media.muted = polygon.muted;
                }
                if media.looping != polygon.looping {
                    player.looping(polygon.looping);
                    media.looping = polygon.looping;
                }
                if let Some(frame) = player.frame() {
                    self.smoke_frames = self.smoke_frames.max(frame.serial);
                    if let Err(e) =
                        self.renderer
                            .upload(polygon.id, frame.size, &frame.pixels, true)
                    {
                        media.error = Some(format!("{e:#}"));
                    }
                }
                if let Some(error) = player.error() {
                    media.error = Some(error);
                }
            }
        }
        while let Ok((id, path, result)) = self.image_results.try_recv() {
            let Some(media) = self
                .media
                .get_mut(&id)
                .filter(|m| m.path == path && m.kind == MediaType::Image)
            else {
                continue;
            };
            match result {
                Ok(image) => {
                    if let Err(e) = self.renderer.upload(
                        id,
                        [image.width(), image.height()],
                        image.as_raw(),
                        false,
                    ) {
                        media.error = Some(format!("{e:#}"));
                    }
                }
                Err(e) => media.error = Some(format!("Immagine non leggibile: {e:#}")),
            }
        }
        if let Some(position) = self
            .media
            .values()
            .filter(|m| m.visible)
            .find_map(|m| m.player.as_ref().map(Player::position))
        {
            self.seek = position;
        }
    }

    fn tick_timeline(&mut self) {
        let Some(timeline) = self.timeline.as_mut() else {
            return;
        };
        let durations: Vec<_> = self.project.scenes.iter().map(|s| s.duration).collect();
        let (index, remaining) = advance_scene(
            &durations,
            timeline.index,
            timeline.started.elapsed().as_secs_f64(),
        );
        if index >= durations.len() {
            self.timeline = None;
            self.scene_progress = 0.;
            return;
        }
        if index != timeline.index {
            timeline.index = index;
            timeline.started = Instant::now() - Duration::from_secs_f64(remaining);
        }
        self.active_scene = Some(self.project.scenes[index].id);
        self.scene_progress = (remaining / durations[index]) as f32;
    }

    pub fn draws(&mut self, size: [f64; 2], assigned: Option<&[Uuid]>) -> Vec<Draw> {
        let solo = self
            .project
            .polygons
            .iter()
            .any(|p| p.solo && p.media_type == MediaType::Video);
        self.project
            .polygons
            .iter()
            .filter(|p| {
                self.project.scene_visible(self.active_scene, p.id)
                    && assigned.is_none_or(|ids| ids.contains(&p.id))
                    && (!solo || p.solo || p.media_type != MediaType::Video)
            })
            .filter_map(|p| self.renderer.draw(p, size))
            .collect()
    }
}

pub fn advance_scene(durations: &[f64], mut index: usize, mut elapsed: f64) -> (usize, f64) {
    while index < durations.len() && elapsed >= durations[index] {
        elapsed -= durations[index];
        index += 1;
    }

    (index, elapsed)
}

impl Drop for App {
    fn drop(&mut self) {
        for (_, mut media) in self.media.drain() {
            if let Some(player) = media.player.take() {
                player.close();
            }
        }
    }
}

impl eframe::App for App {
    fn logic(&mut self, ctx: &egui::Context, frame: &mut eframe::Frame) {
        if ctx.input(|i| i.viewport().close_requested()) && !self.allow_close {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            self.request(Action::Quit);
            if self.pending_action.is_some() {
                ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(false));
            }
        }
        self.receive_job();
        self.tick_timeline();
        self.sync_media();
        self.renderer.retain_surfaces(
            std::iter::once(self.project.size).chain(self.project.outputs.iter().map(|o| o.size)),
        );
        if let Some(warning) = self.outputs.refresh(frame, ctx) {
            self.status = warning;
        }
        for output in self.project.outputs.clone() {
            if self.outputs.is_open(output.id) {
                let draws = self.draws(output.size, Some(&output.polygons));
                self.outputs.update(&output, draws);
            }
        }
        if let Some(started) = self.smoke {
            let elapsed = started.elapsed().as_secs();
            if self.smoke_phase == 0 {
                for output in self.project.outputs.iter().take(2) {
                    self.outputs.open(output);
                }
                if !self.project.scenes.is_empty() {
                    self.timeline = Some(Timeline {
                        index: 0,
                        started: Instant::now(),
                    });
                }
                self.smoke_phase = 1;
            } else if elapsed >= 2 && self.smoke_phase == 1 {
                self.smoke_hidden_frames = self.smoke_frames;
                ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(true));
                self.smoke_phase = 2;
            } else if elapsed >= 3 && self.smoke_phase == 2 {
                self.set_transport(Transport::Pause);
                self.smoke_phase = 3;
            } else if elapsed >= 4 && self.smoke_phase == 3 {
                self.smoke_paused = self
                    .media
                    .values()
                    .filter(|m| m.visible)
                    .filter_map(|m| m.player.as_ref())
                    .all(Player::paused);
                self.seek_all(0.5);
                self.set_transport(Transport::Play);
                self.smoke_phase = 4;
            } else if elapsed >= 5 && self.smoke_phase == 4 {
                self.smoke_progressed = self.smoke_frames > self.smoke_hidden_frames + 10;
                ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(false));
                self.tab = crate::ui::SidebarTab::Outputs;
                Self::capture_smoke(ctx, "outputs");
                self.smoke_phase = 5;
            } else if elapsed >= 6 && self.smoke_phase == 5 {
                self.tab = crate::ui::SidebarTab::Scenes;
                if let Some(output) = self.project.outputs.first() {
                    self.outputs.fullscreen(output, true, ctx);
                }
                self.smoke_phase = 6;
            } else if elapsed >= 7 && self.smoke_phase == 6 {
                self.tab = crate::ui::SidebarTab::Media;
                if let Some(output) = self.project.outputs.first() {
                    self.outputs.fullscreen(output, false, ctx);
                }
                self.set_transport(Transport::Stop);
                self.smoke_phase = 7;
            } else if elapsed >= 8 && self.smoke_phase == 7 {
                self.smoke_stopped = self.seek < 0.02;
                self.set_transport(Transport::Play);
                self.tab = crate::ui::SidebarTab::Layers;
                self.selected = self.project.polygons.first().map(|p| p.id);
                Self::capture_smoke(ctx, "");
                self.smoke_phase = 8;
            } else if elapsed >= 9 && self.smoke_phase == 8 {
                self.show_settings = true;
                self.smoke_phase = 9;
            } else if started.elapsed().as_secs_f64() >= 9.4 && self.smoke_phase == 9 {
                Self::capture_smoke(ctx, "settings");
                self.smoke_phase = 10;
            } else if elapsed >= 10 && self.smoke_phase == 10 {
                self.show_settings = false;
                self.tab = crate::ui::SidebarTab::Outputs;
                ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(900., 600.)));
                self.smoke_phase = 11;
            } else if started.elapsed().as_secs_f64() >= 10.4 && self.smoke_phase == 11 {
                Self::capture_smoke(ctx, "compact");
                self.smoke_phase = 12;
            } else if elapsed >= 11 && !self.allow_close {
                let paints = self.outputs.minimum_paints();
                println!(
                    "VideoMap smoke: {} frame video, {paints} frame per uscita (minimo), {} monitor; editor minimizzato: {}; pausa: {}; stop: {}",
                    self.smoke_frames,
                    self.outputs.displays.len(),
                    self.smoke_progressed,
                    self.smoke_paused,
                    self.smoke_stopped
                );
                self.smoke_success.store(
                    self.smoke_frames > 10
                        && paints > 5
                        && self.smoke_progressed
                        && self.smoke_paused
                        && self.smoke_stopped,
                    std::sync::atomic::Ordering::Release,
                );
                self.allow_close = true;
                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            }
        }
        if self.allow_close {
            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        }
        ctx.request_repaint_after(Duration::from_millis(16));
    }

    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        self.interface(ui);
        for output in self.project.outputs.clone() {
            if self.outputs.is_open(output.id) {
                let draws = self.draws(output.size, Some(&output.polygons));
                self.outputs.show(&ctx, &output, draws);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn timeline_advances_from_selected_scene_once_and_stops_at_end() {
        assert_eq!(advance_scene(&[2., 3., 4.], 1, 3.5), (2, 0.5));
        assert_eq!(advance_scene(&[2., 3., 4.], 1, 8.), (3, 1.));
    }
}
