use crate::{
    app::{Action, App, Drag, Outcome, Timeline},
    geometry::{Fit, contains},
    model::{MediaType, Output, Polygon, Scene},
    project_io, render,
    video::{self, Transport},
};
use eframe::egui::{self, Color32, RichText, Stroke};
use std::{collections::BTreeMap, path::PathBuf, time::Instant};
use uuid::Uuid;

const ACCENT: Color32 = Color32::from_rgb(77, 174, 245);
const MUTED: Color32 = Color32::from_rgb(157, 173, 194);
const BORDER: Color32 = Color32::from_rgb(47, 60, 78);

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum SidebarTab {
    Layers,
    Scenes,
    Outputs,
    Media,
}

pub fn configure_style(ctx: &egui::Context) {
    let mut style = egui::Style::default();
    style.spacing.item_spacing = egui::vec2(8., 8.);
    style.spacing.button_padding = egui::vec2(12., 7.);
    style.spacing.interact_size.y = 32.;
    style.spacing.scroll = egui::style::ScrollStyle::solid();
    style
        .text_styles
        .insert(egui::TextStyle::Heading, egui::FontId::proportional(21.));
    style
        .text_styles
        .insert(egui::TextStyle::Body, egui::FontId::proportional(14.));
    style
        .text_styles
        .insert(egui::TextStyle::Button, egui::FontId::proportional(14.));
    style
        .text_styles
        .insert(egui::TextStyle::Small, egui::FontId::proportional(12.));
    let visuals = &mut style.visuals;
    *visuals = egui::Visuals::dark();
    visuals.panel_fill = Color32::from_rgb(22, 29, 40);
    visuals.window_fill = Color32::from_rgb(27, 35, 47);
    visuals.extreme_bg_color = Color32::from_rgb(14, 20, 29);
    visuals.faint_bg_color = Color32::from_rgb(31, 42, 56);
    visuals.override_text_color = Some(Color32::from_rgb(229, 236, 245));
    visuals.weak_text_color = Some(MUTED);
    visuals.selection.bg_fill = Color32::from_rgb(28, 72, 108);
    visuals.selection.stroke = Stroke::new(1., ACCENT);
    visuals.widgets.noninteractive.bg_stroke = Stroke::new(1., BORDER);
    visuals.widgets.inactive.bg_fill = Color32::from_rgb(38, 50, 67);
    visuals.widgets.inactive.weak_bg_fill = Color32::from_rgb(32, 43, 58);
    visuals.widgets.inactive.bg_stroke = Stroke::new(1., BORDER);
    visuals.widgets.hovered.bg_fill = Color32::from_rgb(46, 69, 92);
    visuals.widgets.hovered.weak_bg_fill = Color32::from_rgb(40, 60, 82);
    visuals.widgets.hovered.bg_stroke = Stroke::new(1., ACCENT);
    visuals.widgets.active.bg_fill = Color32::from_rgb(32, 91, 135);
    visuals.widgets.active.weak_bg_fill = Color32::from_rgb(32, 91, 135);
    ctx.set_theme(egui::Theme::Dark);
    ctx.set_style_of(egui::Theme::Dark, style);
}

fn section(ui: &mut egui::Ui, title: &str, description: &str) {
    ui.label(RichText::new(title).size(18.).strong());
    ui.label(RichText::new(description).small().color(MUTED));
    ui.add_space(6.);
}

fn media_name(kind: MediaType) -> &'static str {
    match kind {
        MediaType::None => "Nessun media",
        MediaType::Image => "Immagine",
        MediaType::Video => "Video",
    }
}

fn media_dialog(title: &str) -> rfd::FileDialog {
    rfd::FileDialog::new().set_title(title).add_filter(
        "Immagini e video",
        &[
            "png", "jpg", "jpeg", "bmp", "gif", "tiff", "webp", "mp4", "mov", "mkv", "avi", "webm",
            "mpeg", "mpg",
        ],
    )
}

fn assignments(ui: &mut egui::Ui, polygons: &[(Uuid, String)], assigned: &mut Vec<Uuid>) -> bool {
    let mut changed = false;
    for (id, name) in polygons {
        let mut selected = assigned.contains(id);
        if ui.checkbox(&mut selected, name).changed() {
            if selected {
                assigned.push(*id);
            } else {
                assigned.retain(|p| p != id);
            }
            changed = true;
        }
    }
    changed
}

impl App {
    pub fn interface(&mut self, ui: &mut egui::Ui) {
        let ctx = ui.ctx().clone();
        egui::Panel::top("toolbar")
            .frame(
                egui::Frame::new()
                    .fill(ui.visuals().panel_fill)
                    .inner_margin(egui::Margin::symmetric(16, 12)),
            )
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.label(RichText::new("VideoMap").size(23.).strong().color(ACCENT));
                    ui.separator();
                    let width = (ui.available_width() - 175.).max(60.);
                    ui.add_sized(
                        [width, 25.],
                        egui::Label::new(RichText::new(&self.project.name).strong()).truncate(),
                    )
                    .on_hover_text(
                        self.path
                            .as_ref()
                            .map(|p| p.display().to_string())
                            .unwrap_or_else(|| "Nessun file salvato".into()),
                    );
                    ui.label(
                        RichText::new(if self.dirty {
                            "Modifiche da salvare"
                        } else if self.path.is_some() {
                            "Salvato"
                        } else {
                            "Nuovo progetto"
                        })
                        .small()
                        .color(if self.dirty {
                            Color32::from_rgb(242, 192, 106)
                        } else {
                            MUTED
                        }),
                    );
                });
                ui.add_space(4.);
                ui.horizontal_wrapped(|ui| {
                    ui.add_enabled_ui(self.job.is_none(), |ui| {
                        ui.menu_button("File", |ui| {
                            if ui.button("Nuovo progetto").clicked() {
                                self.request(Action::New);
                                ui.close();
                            }
                            if ui.button("Apri progetto...").clicked() {
                                self.request(Action::Open);
                                ui.close();
                            }
                            if ui.button("Salva con nome...").clicked() {
                                self.save_project(true);
                                ui.close();
                            }
                            ui.separator();
                            if ui.button("Esporta preset...").clicked() {
                                let project = self.project.clone();
                                self.start_job(move || {
                                    let Some(path) = rfd::FileDialog::new()
                                        .set_title("Esporta preset")
                                        .add_filter("Preset VideoMap", &["vmapout"])
                                        .save_file()
                                    else {
                                        return Ok(None);
                                    };
                                    project_io::export(&project, &path)?;
                                    Ok(Some(Outcome::Message("Preset esportato".into())))
                                });
                                ui.close();
                            }
                        });
                        if ui
                            .button("Salva progetto")
                            .on_hover_text(
                                "Salva il progetto e copia i media nella cartella Assets",
                            )
                            .clicked()
                        {
                            self.save_project(false);
                        }
                        ui.separator();
                        if ui
                            .add(
                                egui::Button::new(
                                    RichText::new("+ Poligono")
                                        .strong()
                                        .color(Color32::from_rgb(13, 30, 45)),
                                )
                                .fill(ACCENT),
                            )
                            .on_hover_text("Crea un quadrilatero al centro dell'area di lavoro")
                            .clicked()
                        {
                            self.add_polygon();
                        }
                        if ui
                            .button("Apri tutte le uscite")
                            .on_hover_text(
                                "Apre una finestra di proiezione per ogni uscita configurata",
                            )
                            .clicked()
                        {
                            for output in &self.project.outputs {
                                self.outputs.open(output);
                            }
                        }
                        if ui
                            .button("Impostazioni")
                            .on_hover_text("Dimensioni del progetto e configurazione VLC")
                            .clicked()
                        {
                            self.show_settings = true;
                        }
                    });
                });
            });
        egui::Panel::bottom("status").show(ui, |ui| {
            ui.horizontal_wrapped(|ui| {
                if self.job.is_some() {
                    ui.spinner();
                    ui.small("Operazione in corso...");
                } else {
                    ui.small(&self.status);
                }
                ui.separator();
                ui.small(format!(
                    "{} livelli  /  {} scene  /  {} uscite aperte",
                    self.project.polygons.len(),
                    self.project.scenes.len(),
                    self.project
                        .outputs
                        .iter()
                        .filter(|o| self.outputs.is_open(o.id))
                        .count()
                ));
            });
        });
        egui::Panel::bottom("transport")
            .frame(
                egui::Frame::new()
                    .fill(ui.visuals().panel_fill)
                    .inner_margin(egui::Margin::symmetric(16, 10)),
            )
            .show(ui, |ui| self.transport_bar(ui));
        egui::Panel::left("lists")
            .default_size(270.)
            .resizable(true)
            .frame(
                egui::Frame::new()
                    .fill(ui.visuals().panel_fill)
                    .inner_margin(16),
            )
            .show(ui, |ui| {
                ui.add_enabled_ui(self.job.is_none(), |ui| {
                    egui::ScrollArea::vertical().show(ui, |ui| self.sidebar(ui));
                });
            });
        egui::Panel::right("properties")
            .default_size(290.)
            .resizable(true)
            .frame(
                egui::Frame::new()
                    .fill(ui.visuals().panel_fill)
                    .inner_margin(16),
            )
            .show(ui, |ui| {
                ui.add_enabled_ui(self.job.is_none(), |ui| {
                    ui.spacing_mut().item_spacing.y = 6.;
                    ui.spacing_mut().interact_size.y = 28.;
                    egui::ScrollArea::vertical()
                        .scroll_bar_visibility(
                            egui::scroll_area::ScrollBarVisibility::AlwaysVisible,
                        )
                        .show(ui, |ui| self.properties(ui));
                });
            });
        egui::CentralPanel::default()
            .frame(egui::Frame::new().fill(Color32::from_rgb(13, 18, 26)).inner_margin(16))
            .show(ui, |ui| {
            ui.horizontal_wrapped(|ui| {
                ui.label(RichText::new("Area di lavoro").strong());
                ui.label(RichText::new(format!("{:.0} x {:.0} px", self.project.size[0], self.project.size[1])).small().color(MUTED));
                if let Some(scene) = self.project.scenes.iter().find(|s| Some(s.id) == self.active_scene) {
                    ui.label(RichText::new(format!("Scena: {}", scene.name)).small().color(ACCENT));
                }
            });
            ui.label(RichText::new("Seleziona un livello. Trascina il poligono per spostarlo, i vertici per deformarlo.").small().color(MUTED));
            ui.add_space(8.);
            ui.add_enabled_ui(self.job.is_none(), |ui| self.designer(ui));
        });
        self.settings_window(&ctx);
        if let Some(action) = self.pending_action {
            egui::Modal::new(egui::Id::unique("discard")).show(&ctx, |ui| {
                ui.heading("Modifiche non salvate");
                ui.label("Salva il progetto prima di continuare, oppure scarta le modifiche.");
                ui.horizontal(|ui| {
                    if ui.button("Scarta e continua").clicked() {
                        self.pending_action = None;
                        self.execute(action);
                    }
                    if ui.button("Annulla").clicked() {
                        self.pending_action = None;
                    }
                });
            });
        }
        if let Some((id, paths)) = self.ambiguities.last().cloned() {
            egui::Modal::new(egui::Id::unique("relink-choice")).show(&ctx, |ui| {
                ui.heading("Piu file con lo stesso nome");
                ui.label("Seleziona il file da ricollegare:");
                egui::ScrollArea::vertical()
                    .max_height(350.)
                    .show(ui, |ui| {
                        for path in paths {
                            if ui.button(path.display().to_string()).clicked() {
                                if let Some(polygon) =
                                    self.project.polygons.iter_mut().find(|p| p.id == id)
                                {
                                    polygon.media = Some(path);
                                    self.dirty = true;
                                }
                                self.ambiguities.pop();
                            }
                        }
                    });
                if ui.button("Ignora").clicked() {
                    self.ambiguities.pop();
                }
            });
        }
    }

    fn save_project(&mut self, as_new: bool) {
        let project = self.project.clone();
        let current = if as_new { None } else { self.path.clone() };
        self.start_job(move || {
            let path = if let Some(path) = current {
                path
            } else {
                let Some(path) = rfd::FileDialog::new()
                    .set_title("Salva progetto")
                    .set_file_name("Progetto.vmap")
                    .add_filter("VideoMap Rust", &["vmap"])
                    .save_file()
                else {
                    return Ok(None);
                };
                path
            };
            Ok(Some(Outcome::Saved(
                project_io::save(&project, &path)?,
                path,
            )))
        });
    }

    fn import(&mut self, id: Uuid, title: &'static str) {
        self.start_job(move || {
            Ok(media_dialog(title)
                .pick_file()
                .map(|path| Outcome::Imported(id, path)))
        });
    }

    fn sidebar(&mut self, ui: &mut egui::Ui) {
        ui.scope(|ui| {
            ui.spacing_mut().button_padding.x = 6.;
            ui.spacing_mut().item_spacing.x = 2.;
            ui.horizontal_wrapped(|ui| {
                for (tab, title) in [
                    (SidebarTab::Layers, "Livelli"),
                    (SidebarTab::Scenes, "Scene"),
                    (SidebarTab::Outputs, "Uscite"),
                    (SidebarTab::Media, "Media"),
                ] {
                    ui.selectable_value(&mut self.tab, tab, title);
                }
            });
        });
        ui.separator();
        ui.add_space(6.);
        match self.tab {
            SidebarTab::Layers => self.layer_list(ui),
            SidebarTab::Scenes => self.scene_list(ui),
            SidebarTab::Outputs => self.output_list(ui),
            SidebarTab::Media => self.media_library(ui),
        }
    }

    fn add_polygon(&mut self) {
        let polygon = Polygon::square(
            format!("Poligono {}", self.project.polygons.len() + 1),
            self.project.size,
        );
        self.selected = Some(polygon.id);
        for scene in &mut self.project.scenes {
            scene.polygons.push(polygon.id);
        }
        for output in &mut self.project.outputs {
            output.polygons.push(polygon.id);
        }
        self.project.polygons.push(polygon);
        self.tab = SidebarTab::Layers;
        self.dirty = true;
    }

    fn layer_list(&mut self, ui: &mut egui::Ui) {
        section(
            ui,
            "Livelli",
            "Seleziona un poligono per modificarne media e geometria.",
        );
        if self.project.polygons.is_empty() {
            ui.label("Non ci sono ancora poligoni.");
        }
        for polygon in self.project.polygons.iter().rev() {
            let missing = polygon.media.as_ref().is_some_and(|p| !p.is_file());
            let visibility = if missing {
                "Media mancante"
            } else if self.project.scene_visible(self.active_scene, polygon.id) {
                "In scena"
            } else {
                "Fuori scena"
            };
            let title = format!(
                "{}\n{}  |  {visibility}",
                polygon.name,
                media_name(polygon.media_type)
            );
            if ui
                .add_sized(
                    [ui.available_width(), 54.],
                    egui::Button::selectable(self.selected == Some(polygon.id), title).wrap(),
                )
                .clicked()
            {
                self.selected = Some(polygon.id);
            }
        }
        ui.add_space(6.);
        if ui.button("+ Aggiungi poligono").clicked() {
            self.add_polygon();
        }
        ui.small("Il primo livello nell'elenco e in primo piano.");
    }

    fn scene_list(&mut self, ui: &mut egui::Ui) {
        section(ui, "Scene", "Organizza i livelli in sequenze temporizzate.");
        ui.horizontal(|ui| {
            if ui.button("+ Nuova scena").clicked() {
                let scene = Scene {
                    id: Uuid::new_v4(),
                    name: format!("Scena {}", self.project.scenes.len() + 1),
                    duration: 5.,
                    polygons: self.project.polygons.iter().map(|p| p.id).collect(),
                };
                self.active_scene = Some(scene.id);
                self.selected_scene = Some(scene.id);
                self.project.scenes.push(scene);
                self.timeline = None;
                self.dirty = true;
            }
            if ui
                .add_enabled(self.selected_scene.is_some(), egui::Button::new("Elimina"))
                .clicked()
            {
                self.project
                    .scenes
                    .retain(|s| Some(s.id) != self.selected_scene);
                self.selected_scene = self.project.scenes.first().map(|s| s.id);
                self.active_scene = self.selected_scene;
                self.timeline = None;
                self.dirty = true;
            }
        });
        for scene in &self.project.scenes {
            if ui
                .add_sized(
                    [ui.available_width(), 54.],
                    egui::Button::selectable(
                        self.selected_scene == Some(scene.id),
                        format!(
                            "{}\n{:.1} secondi{}",
                            scene.name,
                            scene.duration,
                            if self.active_scene == Some(scene.id) {
                                "  |  Attiva"
                            } else {
                                ""
                            }
                        ),
                    )
                    .wrap(),
                )
                .clicked()
            {
                self.selected_scene = Some(scene.id);
                self.active_scene = self.selected_scene;
                self.timeline = None;
            }
        }
        if self.project.scenes.is_empty() {
            ui.label("Aggiungi una scena per creare una sequenza.");
        }
        ui.add_space(8.);
        ui.add(egui::ProgressBar::new(self.scene_progress).show_percentage());
        ui.small(if self.timeline.is_some() {
            "Sequenza in riproduzione"
        } else {
            "Sequenza ferma"
        });
    }

    fn scene_properties(&mut self, ui: &mut egui::Ui) {
        section(
            ui,
            "Scena selezionata",
            "Scegli quali livelli mostrare e per quanto tempo.",
        );
        let names: Vec<_> = self
            .project
            .polygons
            .iter()
            .map(|p| (p.id, p.name.clone()))
            .collect();
        if let Some(scene) = self
            .project
            .scenes
            .iter_mut()
            .find(|s| Some(s.id) == self.selected_scene)
        {
            ui.label("Nome della scena");
            self.dirty |= ui
                .add(egui::TextEdit::singleline(&mut scene.name).desired_width(f32::INFINITY))
                .changed();
            ui.horizontal(|ui| {
                ui.label("Durata (s)");
                self.dirty |= ui
                    .add(
                        egui::DragValue::new(&mut scene.duration)
                            .range(0.1..=86400.)
                            .speed(0.1),
                    )
                    .changed();
            });
            ui.separator();
            egui::CollapsingHeader::new("Livelli della scena")
                .default_open(true)
                .show(ui, |ui| {
                    self.dirty |= assignments(ui, &names, &mut scene.polygons);
                });
            ui.small("Nessuna selezione: mostra tutti i livelli.");
        } else {
            ui.label("Seleziona o crea una scena nel pannello a sinistra.");
        }
        ui.add_space(8.);
        ui.add_enabled_ui(self.selected_scene.is_some(), |ui| {
            if ui
                .button("Mostra scena")
                .on_hover_text("Applica la scena all'editor e a tutte le uscite")
                .clicked()
            {
                self.active_scene = self.selected_scene;
                self.timeline = None;
            }
            if ui
                .button("Riproduci sequenza")
                .on_hover_text("Riproduce dalla scena selezionata fino all'ultima")
                .clicked()
                && let Some(index) = self
                    .project
                    .scenes
                    .iter()
                    .position(|s| Some(s.id) == self.selected_scene)
            {
                self.timeline = Some(Timeline {
                    index,
                    started: Instant::now(),
                });
                self.set_transport(Transport::Play);
            }
            if ui
                .add_enabled(
                    self.timeline.is_some(),
                    egui::Button::new("Interrompi sequenza"),
                )
                .clicked()
            {
                self.timeline = None;
                self.scene_progress = 0.;
            }
        });
    }

    fn output_list(&mut self, ui: &mut egui::Ui) {
        section(
            ui,
            "Uscite",
            "Ogni uscita ha una finestra e un monitor indipendenti.",
        );
        ui.horizontal(|ui| {
            if ui.button("+ Nuova uscita").clicked() {
                let output = Output {
                    id: Uuid::new_v4(),
                    name: format!("Output {}", self.project.outputs.len() + 1),
                    size: self.project.size,
                    polygons: self.project.polygons.iter().map(|p| p.id).collect(),
                    monitor: None,
                };
                self.selected_output = Some(output.id);
                self.project.outputs.push(output);
                self.dirty = true;
            }
            if ui
                .add_enabled(self.selected_output.is_some(), egui::Button::new("Elimina"))
                .clicked()
            {
                if let Some(id) = self.selected_output {
                    self.outputs.close(id);
                }
                self.project
                    .outputs
                    .retain(|o| Some(o.id) != self.selected_output);
                self.selected_output = self.project.outputs.first().map(|o| o.id);
                self.dirty = true;
            }
        });
        for output in &self.project.outputs {
            if ui
                .add_sized(
                    [ui.available_width(), 54.],
                    egui::Button::selectable(
                        self.selected_output == Some(output.id),
                        format!(
                            "{}\n{:.0} x {:.0}  |  {}",
                            output.name,
                            output.size[0],
                            output.size[1],
                            if self.outputs.is_open(output.id) {
                                "Aperta"
                            } else {
                                "Chiusa"
                            }
                        ),
                    )
                    .wrap(),
                )
                .clicked()
            {
                self.selected_output = Some(output.id);
            }
        }
        if self.project.outputs.is_empty() {
            ui.label("Crea un'uscita per aprire una finestra di proiezione.");
        }
        ui.add_space(8.);
        ui.small("Seleziona un'uscita per assegnare livelli e monitor.");
    }

    fn output_controls(&mut self, ui: &mut egui::Ui) {
        if let Some(output) = self
            .project
            .outputs
            .iter()
            .find(|o| Some(o.id) == self.selected_output)
            .cloned()
        {
            ui.horizontal_wrapped(|ui| {
                if ui
                    .add_enabled(
                        !self.outputs.is_open(output.id),
                        egui::Button::new("Apri finestra"),
                    )
                    .clicked()
                {
                    self.outputs.open(&output);
                }
                if ui
                    .add_enabled(self.outputs.is_open(output.id), egui::Button::new("Chiudi"))
                    .clicked()
                {
                    self.outputs.close(output.id);
                }
            });
            let mut fullscreen = self.outputs.is_fullscreen(output.id);
            if ui
                .checkbox(&mut fullscreen, "Schermo intero")
                .on_hover_text(
                    "Usa il monitor scelto senza cambiarne la risoluzione. Esc per uscire.",
                )
                .changed()
            {
                self.outputs.fullscreen(&output, fullscreen, ui.ctx());
            }
            ui.small("Esc torna in finestra. Le proporzioni sono sempre preservate.");
            ui.separator();
            let names: Vec<_> = self
                .project
                .polygons
                .iter()
                .map(|p| (p.id, p.name.clone()))
                .collect();
            if let Some(output) = self
                .project
                .outputs
                .iter_mut()
                .find(|o| Some(o.id) == self.selected_output)
            {
                egui::CollapsingHeader::new("Livelli proiettati")
                    .default_open(true)
                    .show(ui, |ui| {
                        self.dirty |= assignments(ui, &names, &mut output.polygons);
                    });
                if output.polygons.is_empty() {
                    ui.colored_label(
                        Color32::from_rgb(242, 192, 106),
                        "Nessun livello assegnato: l'uscita resta nera.",
                    );
                }
            }
        }
    }

    fn media_library(&mut self, ui: &mut egui::Ui) {
        section(
            ui,
            "Libreria media",
            "File utilizzati nel progetto e collegamenti da ripristinare.",
        );
        if ui
            .button("Ricollega file mancanti...")
            .on_hover_text("Cerca i file in una cartella e nelle sue sottocartelle")
            .clicked()
        {
            self.start_job(|| {
                let Some(folder) = rfd::FileDialog::new()
                    .set_title("Cartella dei media mancanti")
                    .pick_folder()
                else {
                    return Ok(None);
                };
                Ok(Some(Outcome::Scanned(project_io::scan(&folder)?)))
            });
        }
        ui.separator();
        let mut library = BTreeMap::<PathBuf, (MediaType, usize)>::new();
        for polygon in &self.project.polygons {
            if let Some(path) = &polygon.media {
                library
                    .entry(path.clone())
                    .or_insert((polygon.media_type, 0))
                    .1 += 1;
            }
        }
        if library.is_empty() {
            ui.label("Nessun file importato.");
            ui.small("Seleziona un livello e usa Importa media nelle sue proprieta.");
        }
        for (path, (kind, count)) in library {
            egui::Frame::new()
                .fill(ui.visuals().faint_bg_color)
                .inner_margin(10)
                .corner_radius(6)
                .show(ui, |ui| {
                    ui.add(
                        egui::Label::new(
                            RichText::new(path.file_name().unwrap_or_default().to_string_lossy())
                                .strong(),
                        )
                        .truncate(),
                    )
                    .on_hover_text(path.display().to_string());
                    ui.small(format!("{}  |  Utilizzi: {count}", media_name(kind)));
                    if !path.is_file() {
                        ui.colored_label(Color32::LIGHT_RED, "File mancante");
                    }
                });
        }
    }

    fn properties(&mut self, ui: &mut egui::Ui) {
        match self.tab {
            SidebarTab::Layers => self.polygon_properties(ui),
            SidebarTab::Scenes => self.scene_properties(ui),
            SidebarTab::Outputs => self.output_properties(ui),
            SidebarTab::Media => {
                section(
                    ui,
                    "File del progetto",
                    "Una libreria unica per editor e uscite.",
                );
                ui.label("Per importare o sostituire un file, seleziona il suo livello.");
                ui.separator();
                ui.label(RichText::new("Progetti portabili").strong());
                ui.label("Salvando il progetto, i media vengono copiati nella cartella Assets.");
                ui.small("Per spostare il lavoro, copia il file .vmap insieme ad Assets.");
                ui.separator();
                ui.label(RichText::new("File mancanti").strong());
                ui.label("Usa Ricollega file mancanti per cercarli in una cartella. In caso di nomi uguali potrai scegliere il file corretto.");
                if ui.button("Impostazioni video").clicked() {
                    self.show_settings = true;
                }
            }
        }
    }

    fn output_properties(&mut self, ui: &mut egui::Ui) {
        section(
            ui,
            "Uscita selezionata",
            "Configura la superficie di proiezione.",
        );
        if let Some(output) = self
            .project
            .outputs
            .iter_mut()
            .find(|o| Some(o.id) == self.selected_output)
        {
            ui.label("Nome dell'uscita");
            self.dirty |= ui
                .add(egui::TextEdit::singleline(&mut output.name).desired_width(f32::INFINITY))
                .changed();
            ui.add_space(4.);
            for (title, value) in ["Larghezza", "Altezza"].into_iter().zip(&mut output.size) {
                ui.horizontal(|ui| {
                    ui.label(title);
                    self.dirty |= ui
                        .add(egui::DragValue::new(value).range(1.0..=65536.))
                        .changed();
                    ui.small("px");
                });
            }
            ui.separator();
            ui.label("Monitor di destinazione");
            let previous = output.monitor.clone();
            egui::ComboBox::from_id_salt("monitor")
                .selected_text(
                    output
                        .monitor
                        .as_ref()
                        .map(|p| p.name.as_str())
                        .unwrap_or("Principale"),
                )
                .show_ui(ui, |ui| {
                    ui.selectable_value(&mut output.monitor, None, "Principale");
                    for display in &self.outputs.displays {
                        let p = &display.preference;
                        ui.selectable_value(
                            &mut output.monitor,
                            Some(p.clone()),
                            format!(
                                "{} {}x{} ({},{})",
                                p.name, p.size[0], p.size[1], p.position[0], p.position[1]
                            ),
                        );
                    }
                });
            if output.monitor != previous {
                self.dirty = true;
                if self.outputs.is_fullscreen(output.id) {
                    self.outputs.fullscreen(output, true, ui.ctx());
                }
            }
            ui.add_space(8.);
        } else {
            ui.label("Seleziona o crea un'uscita nel pannello a sinistra.");
        }
        self.output_controls(ui);
    }

    fn polygon_properties(&mut self, ui: &mut egui::Ui) {
        section(
            ui,
            "Proprieta del livello",
            "Media, riproduzione e ordine di sovrapposizione.",
        );
        let Some(index) = self
            .project
            .polygons
            .iter()
            .position(|p| Some(p.id) == self.selected)
        else {
            ui.add_space(8.);
            ui.label(RichText::new("Nessun livello selezionato").strong());
            ui.label(
                "Fai clic su un poligono nell'area di lavoro oppure scegli un livello a sinistra.",
            );
            return;
        };
        let id = self.project.polygons[index].id;
        ui.label("Nome del livello");
        self.dirty |= ui
            .add(
                egui::TextEdit::singleline(&mut self.project.polygons[index].name)
                    .desired_width(f32::INFINITY),
            )
            .changed();
        ui.small(format!(
            "{} vertici",
            self.project.polygons[index].points.len()
        ));
        ui.separator();
        ui.label(RichText::new("Media").strong());
        if let Some(path) = &self.project.polygons[index].media {
            ui.add(
                egui::Label::new(path.file_name().unwrap_or_default().to_string_lossy()).truncate(),
            )
            .on_hover_text(path.display().to_string());
            ui.small(media_name(self.project.polygons[index].media_type));
        } else {
            ui.label(RichText::new("Nessuna immagine o video assegnato.").color(MUTED));
        }
        if let Some(error) = self.media.get(&id).and_then(|m| m.error.as_ref()) {
            egui::Frame::new()
                .fill(Color32::from_rgb(63, 35, 40))
                .inner_margin(10)
                .corner_radius(6)
                .show(ui, |ui| {
                    ui.colored_label(Color32::LIGHT_RED, error);
                });
        }
        ui.horizontal_wrapped(|ui| {
            if ui
                .button(if self.project.polygons[index].media.is_some() {
                    "Sostituisci..."
                } else {
                    "Importa media..."
                })
                .on_hover_text("Assegna un'immagine o un video al livello")
                .clicked()
            {
                self.import(id, "Importa immagine o video");
            }
            if self.project.polygons[index].media.is_some()
                && ui
                    .button("Ricollega...")
                    .on_hover_text("Scegli la nuova posizione del file")
                    .clicked()
            {
                self.import(id, "Ricollega media");
            }
        });
        let polygon = &mut self.project.polygons[index];
        if polygon.media_type == MediaType::Video {
            ui.separator();
            ui.label(RichText::new("Riproduzione").strong());
            self.dirty |= ui
                .checkbox(&mut polygon.solo, "Solo questo video")
                .on_hover_text("Nasconde gli altri video senza nascondere le immagini")
                .changed();
            self.dirty |= ui
                .checkbox(&mut polygon.muted, "Audio disattivato")
                .changed();
            self.dirty |= ui
                .checkbox(&mut polygon.looping, "Ripeti in ciclo")
                .changed();
        }
        ui.separator();
        ui.label(RichText::new("Sovrapposizione").strong());
        let mut movement = 0;
        ui.horizontal_wrapped(|ui| {
            if ui
                .add_enabled(index > 0, egui::Button::new("Indietro"))
                .on_hover_text("Sposta il livello dietro agli altri")
                .clicked()
            {
                movement = -1;
            }
            if ui
                .add_enabled(
                    index + 1 < self.project.polygons.len(),
                    egui::Button::new("Avanti"),
                )
                .on_hover_text("Porta il livello davanti agli altri")
                .clicked()
            {
                movement = 1;
            }
        });
        if movement != 0 {
            self.project
                .polygons
                .swap(index, (index as isize + movement) as usize);
            self.dirty = true;
        }
        ui.separator();
        if ui
            .button(RichText::new("Elimina livello").color(Color32::LIGHT_RED))
            .clicked()
        {
            self.project.remove_polygon(id);
            self.selected = None;
            self.dirty = true;
        }
    }

    fn transport_bar(&mut self, ui: &mut egui::Ui) {
        let has_video = self
            .project
            .polygons
            .iter()
            .any(|p| p.media_type == MediaType::Video);
        ui.horizontal_wrapped(|ui| {
            ui.label(RichText::new("Video").strong());
            ui.add_enabled_ui(has_video && self.engine.is_some(), |ui| {
                for (title, state) in [
                    ("Riproduci", Transport::Play),
                    ("Pausa", Transport::Pause),
                    ("Ferma", Transport::Stop),
                ] {
                    if ui
                        .add(egui::Button::new(title).selected(self.transport == state))
                        .clicked()
                    {
                        self.set_transport(state);
                    }
                }
                ui.separator();
                ui.spacing_mut().slider_width = (ui.available_width() - 150.).clamp(100., 320.);
                if ui
                    .add(
                        egui::Slider::new(&mut self.seek, 0.0..=1.0)
                            .show_value(false)
                            .text("Posizione"),
                    )
                    .on_hover_text("Posizione di riproduzione dei video attivi")
                    .changed()
                {
                    self.seek_all(self.seek);
                }
            });
            ui.label(
                RichText::new(if !has_video {
                    "Nessun video"
                } else {
                    match self.transport {
                        Transport::Play => "In riproduzione",
                        Transport::Pause => "In pausa",
                        Transport::Stop => "Fermo",
                    }
                })
                .small()
                .color(MUTED),
            );
            if has_video && self.engine.is_none() && ui.button("Configura VLC").clicked() {
                self.show_settings = true;
            }
        });
    }

    fn settings_window(&mut self, ctx: &egui::Context) {
        if !self.show_settings {
            return;
        }
        let mut open = self.show_settings;
        egui::Window::new("Impostazioni")
            .open(&mut open)
            .default_width(440.)
            .default_height(520.)
            .max_height(540.)
            .vscroll(true)
            .scroll_bar_visibility(egui::scroll_area::ScrollBarVisibility::AlwaysVisible)
            .collapsible(false)
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .fade_in(false)
            .fade_out(false)
            .show(ctx, |ui| {
                ui.add_enabled_ui(self.job.is_none(), |ui| {
                    section(
                        ui,
                        "Progetto",
                        "Le dimensioni definiscono le coordinate dell'area di lavoro.",
                    );
                    ui.label("Nome del progetto");
                    self.dirty |= ui
                        .add(
                            egui::TextEdit::singleline(&mut self.project.name)
                                .desired_width(f32::INFINITY),
                        )
                        .changed();
                    for (title, value) in ["Larghezza", "Altezza"]
                        .into_iter()
                        .zip(&mut self.project.size)
                    {
                        ui.horizontal(|ui| {
                            ui.label(title);
                            self.dirty |= ui
                                .add(egui::DragValue::new(value).range(1.0..=65536.))
                                .changed();
                            ui.small("px");
                        });
                    }
                    ui.separator();
                    section(
                        ui,
                        "Riproduzione video",
                        "Installa VLC 3 separatamente, con la stessa architettura dell'app.",
                    );
                    ui.label("Cartella di VLC");
                    ui.add(
                        egui::TextEdit::singleline(&mut self.vlc_edit).desired_width(f32::INFINITY),
                    )
                    .on_hover_text("Esempio macOS: /Applications/VLC.app");
                    ui.small("Su macOS: /Applications/VLC.app. E necessario un riavvio.");
                    if self.dirty {
                        ui.colored_label(
                            Color32::from_rgb(242, 192, 106),
                            "Salva il progetto prima di applicare la configurazione VLC.",
                        );
                    }
                    ui.horizontal_wrapped(|ui| {
                        if ui.button("Sfoglia").clicked() {
                            self.start_job(|| {
                                Ok(rfd::FileDialog::new()
                                    .set_title("Cartella VLC o VLC.app")
                                    .pick_folder()
                                    .map(Outcome::VlcPath))
                            });
                        }
                        if ui.button("Applica").clicked() {
                            self.configure_vlc();
                        }
                        if ui.button("Rileva automaticamente").clicked() {
                            self.vlc_edit.clear();
                            self.configure_vlc();
                        }
                    });
                    ui.small(&self.vlc_status);
                    if ui
                        .button("Ricarica media")
                        .on_hover_text("Riprova il caricamento di immagini e video")
                        .clicked()
                    {
                        self.media.clear();
                        self.renderer.clear();
                        if self.engine.is_none() {
                            match video::Engine::load(self.settings.vlc_path.as_deref()) {
                                Ok(engine) => {
                                    self.vlc_status = format!("LibVLC {}", engine.version);
                                    self.engine = Some(engine);
                                }
                                Err(e) => self.vlc_status = format!("{e:#}"),
                            }
                        }
                    }
                });
            });
        self.show_settings = open;
    }

    fn configure_vlc(&mut self) {
        if self.dirty {
            self.status = "Salva il progetto prima di riavviare per configurare VLC".into();
            return;
        }
        self.settings.vlc_path =
            (!self.vlc_edit.trim().is_empty()).then(|| PathBuf::from(self.vlc_edit.trim()));
        let result = (|| {
            self.settings.save()?;
            if let Some(_child) = video::relaunch(
                self.settings.vlc_path.as_deref(),
                true,
                self.path.as_deref(),
            )? {
                self.allow_close = true;
            } else {
                self.engine = Some(video::Engine::load(self.settings.vlc_path.as_deref())?);
                self.media.clear();
                self.vlc_status = "LibVLC configurato".into();
            }
            anyhow::Ok(())
        })();
        if let Err(e) = result {
            self.status = format!("{e:#}");
        }
    }

    fn designer(&mut self, ui: &mut egui::Ui) {
        let available = ui.available_size().max(egui::vec2(1., 1.));
        let (rect, response) = ui.allocate_exact_size(available, egui::Sense::click_and_drag());
        let fit = Fit::new(rect, self.project.size);
        let surface = fit.rect(self.project.size);
        let draws = self.draws(self.project.size, None);
        let painter = ui.painter().with_clip_rect(surface.intersect(rect));
        render::paint(ui, surface, &draws);
        if draws.is_empty() {
            let grid = Stroke::new(1., Color32::from_rgb(25, 34, 46));
            let mut x = surface.left() + 40.;
            while x < surface.right() {
                painter.line_segment(
                    [
                        egui::pos2(x, surface.top()),
                        egui::pos2(x, surface.bottom()),
                    ],
                    grid,
                );
                x += 40.;
            }
            let mut y = surface.top() + 40.;
            while y < surface.bottom() {
                painter.line_segment(
                    [
                        egui::pos2(surface.left(), y),
                        egui::pos2(surface.right(), y),
                    ],
                    grid,
                );
                y += 40.;
            }
        }
        painter.rect_stroke(
            surface,
            0.,
            Stroke::new(1., BORDER),
            egui::StrokeKind::Inside,
        );
        if response.drag_started()
            && let Some(pointer) = ui.input(|i| i.pointer.press_origin())
        {
            let p = fit.project(pointer);
            let vertex = self
                .project
                .polygons
                .iter()
                .find(|polygon| Some(polygon.id) == self.selected)
                .and_then(|polygon| {
                    polygon
                        .points
                        .iter()
                        .position(|p| fit.screen(*p).distance(pointer) < 10.)
                });
            let id = if vertex.is_some() {
                self.selected
            } else {
                self.project
                    .polygons
                    .iter()
                    .rev()
                    .find(|polygon| {
                        self.project.scene_visible(self.active_scene, polygon.id)
                            && contains(&polygon.points, p)
                    })
                    .map(|polygon| polygon.id)
            };
            self.selected = id;
            if id.is_some() {
                self.tab = SidebarTab::Layers;
            }
            self.drag = id.and_then(|id| {
                self.project
                    .polygons
                    .iter()
                    .find(|polygon| polygon.id == id)
                    .map(|polygon| Drag {
                        id,
                        vertex,
                        start: p,
                        original: polygon.points.clone(),
                    })
            });
        }
        if response.dragged()
            && let (Some(drag), Some(pointer)) = (&self.drag, response.interact_pointer_pos())
        {
            let p = fit.project(pointer);
            let delta = [p[0] - drag.start[0], p[1] - drag.start[1]];
            let mut candidate = drag.original.clone();
            for (i, p) in candidate.iter_mut().enumerate() {
                if drag.vertex.is_none_or(|vertex| vertex == i) {
                    p[0] += delta[0];
                    p[1] += delta[1];
                }
            }
            if let Some(polygon) = self.project.polygons.iter_mut().find(|p| p.id == drag.id) {
                if polygon.set_points(candidate) {
                    self.dirty = true;
                } else {
                    self.status =
                        "Geometria non valida: mantenuta l'ultima posizione valida".into();
                }
            }
        }
        if response.drag_stopped() {
            self.drag = None;
        }
        if response.clicked()
            && let Some(pointer) = response.interact_pointer_pos()
        {
            let p = fit.project(pointer);
            self.selected = self
                .project
                .polygons
                .iter()
                .rev()
                .find(|polygon| {
                    self.project.scene_visible(self.active_scene, polygon.id)
                        && contains(&polygon.points, p)
                })
                .map(|polygon| polygon.id);
            if self.selected.is_some() {
                self.tab = SidebarTab::Layers;
            }
        }
        for polygon in &self.project.polygons {
            if !self.project.scene_visible(self.active_scene, polygon.id) {
                continue;
            }
            let selected = Some(polygon.id) == self.selected;
            let color = if selected {
                ACCENT
            } else {
                Color32::LIGHT_GRAY
            };
            let points: Vec<_> = polygon.points.iter().map(|p| fit.screen(*p)).collect();
            painter.add(egui::Shape::closed_line(
                points,
                Stroke::new(if selected { 2. } else { 1. }, color),
            ));
            if selected {
                for p in &polygon.points {
                    painter.circle_filled(fit.screen(*p), 6., ACCENT);
                    painter.circle_stroke(fit.screen(*p), 6., Stroke::new(1.5, Color32::WHITE));
                }
            }
        }
        if self.project.polygons.is_empty() {
            painter.text(
                surface.center() - egui::vec2(0., 40.),
                egui::Align2::CENTER_CENTER,
                "Inizia da un poligono",
                egui::FontId::proportional(21.),
                Color32::from_rgb(229, 236, 245),
            );
            painter.text(
                surface.center() - egui::vec2(0., 10.),
                egui::Align2::CENTER_CENTER,
                "Importa un media e trascina i vertici.",
                egui::FontId::proportional(12.),
                MUTED,
            );
            if ui
                .put(
                    egui::Rect::from_center_size(
                        surface.center() + egui::vec2(0., 35.),
                        egui::vec2(190., 36.),
                    ),
                    egui::Button::new(
                        RichText::new("+ Primo poligono")
                            .color(Color32::from_rgb(13, 30, 45))
                            .strong(),
                    )
                    .fill(ACCENT),
                )
                .clicked()
            {
                self.add_polygon();
            }
        }
    }
}
