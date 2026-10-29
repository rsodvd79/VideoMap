use crate::{
    geometry::Fit,
    model::{MonitorPreference, Output},
    render::{self, Draw},
};
use eframe::egui::{self, ViewportCommand, ViewportId};
use std::{
    collections::HashMap,
    sync::{Arc, Mutex},
    time::Duration,
};
use uuid::Uuid;

#[derive(Clone)]
pub struct Display {
    pub preference: MonitorPreference,
    pub scale: f64,
    pub primary: bool,
}

#[derive(Clone)]
struct Window {
    open: bool,
    fullscreen: bool,
    target: Option<MonitorPreference>,
    output: Output,
    draws: Vec<Draw>,
    paints: u64,
}

#[derive(Default)]
pub struct Outputs {
    windows: HashMap<Uuid, Arc<Mutex<Window>>>,
    pub displays: Vec<Display>,
}

pub fn match_monitor(preference: &MonitorPreference, displays: &[Display]) -> Option<usize> {
    displays
        .iter()
        .position(|m| m.preference == *preference)
        .or_else(|| {
            let matches: Vec<_> = displays
                .iter()
                .enumerate()
                .filter(|(_, m)| {
                    m.preference.name == preference.name && m.preference.size == preference.size
                })
                .collect();
            if matches.len() == 1 {
                Some(matches[0].0)
            } else {
                None
            }
        })
}

fn viewport(id: Uuid) -> ViewportId {
    ViewportId::from_hash_of(id)
}

fn fallback_window(display: &Display, pixels_per_point: f32) -> (egui::Pos2, egui::Vec2) {
    let position = egui::pos2(
        display.preference.position[0] as f32,
        display.preference.position[1] as f32,
    ) + egui::vec2(40., 40.) * display.scale as f32;
    let size = (egui::vec2(800., 450.) * display.scale as f32).min(
        egui::vec2(
            display.preference.size[0] as f32,
            display.preference.size[1] as f32,
        ) * 0.8,
    );
    (position / pixels_per_point, size / pixels_per_point)
}

impl Outputs {
    pub fn refresh(&mut self, frame: &eframe::Frame, ctx: &egui::Context) -> Option<String> {
        if let Some(window) = frame.winit_window() {
            let primary = window.primary_monitor();
            self.displays = window
                .available_monitors()
                .map(|m| {
                    let position = m.position();
                    let size = m.size();
                    Display {
                        preference: MonitorPreference {
                            name: m.name().unwrap_or_else(|| "Monitor".into()),
                            size: [size.width, size.height],
                            position: [position.x, position.y],
                        },
                        scale: m.scale_factor(),
                        primary: primary.as_ref().is_some_and(|p| *p == m),
                    }
                })
                .collect();
        }
        let mut warning = None;
        for (id, window) in &self.windows {
            if let Ok(mut state) = window.lock()
                && state.fullscreen
                && state
                    .target
                    .as_ref()
                    .is_some_and(|target| !self.displays.iter().any(|d| d.preference == *target))
            {
                state.fullscreen = false;
                state.target = None;
                let id = viewport(*id);
                ctx.send_viewport_cmd_to(id, ViewportCommand::Fullscreen(false));
                ctx.send_viewport_cmd_to(id, ViewportCommand::Decorations(true));
                if let Some(display) = self
                    .displays
                    .iter()
                    .find(|d| d.primary)
                    .or(self.displays.first())
                {
                    let scale = ctx.input_for(id, |i| i.pixels_per_point);
                    let (position, size) = fallback_window(display, scale);
                    ctx.send_viewport_cmd_to(id, ViewportCommand::InnerSize(size));
                    ctx.send_viewport_cmd_to(id, ViewportCommand::OuterPosition(position));
                }
                warning = Some(format!(
                    "Monitor scollegato: {} riportato sul principale",
                    state.output.name
                ));
            }
        }
        warning
    }

    pub fn is_open(&self, id: Uuid) -> bool {
        self.windows
            .get(&id)
            .is_some_and(|w| w.lock().is_ok_and(|s| s.open))
    }
    pub fn is_fullscreen(&self, id: Uuid) -> bool {
        self.windows
            .get(&id)
            .is_some_and(|w| w.lock().is_ok_and(|s| s.fullscreen))
    }
    pub fn open(&mut self, output: &Output) {
        self.windows
            .entry(output.id)
            .and_modify(|w| {
                if let Ok(mut state) = w.lock() {
                    state.open = true;
                }
            })
            .or_insert_with(|| {
                Arc::new(Mutex::new(Window {
                    open: true,
                    fullscreen: false,
                    target: None,
                    output: output.clone(),
                    draws: vec![],
                    paints: 0,
                }))
            });
    }
    pub fn close(&mut self, id: Uuid) {
        self.windows.remove(&id);
    }
    pub fn clear(&mut self) {
        self.windows.clear();
    }
    pub fn update(&self, output: &Output, draws: Vec<Draw>) {
        if let Some(window) = self.windows.get(&output.id)
            && let Ok(mut state) = window.lock()
        {
            state.output = output.clone();
            state.draws = draws;
        }
    }
    pub fn minimum_paints(&self) -> u64 {
        self.windows
            .values()
            .filter_map(|w| w.lock().ok().map(|s| s.paints))
            .min()
            .unwrap_or_default()
    }
    pub fn fullscreen(&mut self, output: &Output, value: bool, ctx: &egui::Context) {
        self.open(output);
        if let Some(window) = self.windows.get(&output.id)
            && let Ok(mut state) = window.lock()
        {
            let target = output
                .monitor
                .as_ref()
                .and_then(|p| match_monitor(p, &self.displays))
                .or_else(|| self.displays.iter().position(|d| d.primary))
                .or_else(|| (!self.displays.is_empty()).then_some(0));
            state.fullscreen = value && target.is_some();
            state.target = target.map(|i| self.displays[i].preference.clone());
            ctx.send_viewport_cmd_to(
                viewport(output.id),
                if let Some(target) = target.filter(|_| value) {
                    ViewportCommand::SetMonitor(target)
                } else {
                    ViewportCommand::Fullscreen(false)
                },
            );
            ctx.send_viewport_cmd_to(
                viewport(output.id),
                ViewportCommand::Decorations(!state.fullscreen),
            );
        }
    }
    pub fn show(&mut self, ctx: &egui::Context, output: &Output, draws: Vec<Draw>) {
        let Some(window) = self.windows.get(&output.id).cloned() else {
            return;
        };
        let (open, fullscreen, target) = {
            let Ok(mut state) = window.lock() else {
                return;
            };
            state.output = output.clone();
            state.draws = draws;
            (state.open, state.fullscreen, state.target.clone())
        };
        if !open {
            return;
        }
        let mut builder = egui::ViewportBuilder::default()
            .with_title(format!("VideoMap - {}", output.name))
            .with_inner_size([800., 450.])
            .with_fullscreen(fullscreen)
            .with_decorations(!fullscreen);
        if fullscreen {
            builder.monitor = target
                .as_ref()
                .and_then(|p| match_monitor(p, &self.displays));
        }
        ctx.show_viewport_deferred(viewport(output.id), builder, move |ctx, _| {
            let snapshot = match window.lock() {
                Ok(state) => state.clone(),
                Err(_) => return,
            };
            if ctx.input(|i| i.viewport().close_requested()) {
                if let Ok(mut state) = window.lock() {
                    state.open = false;
                }
                ctx.request_repaint_of(ViewportId::ROOT);
            }
            if ctx.input(|i| i.key_pressed(egui::Key::Escape)) && snapshot.fullscreen {
                if let Ok(mut state) = window.lock() {
                    state.fullscreen = false;
                }
                ctx.send_viewport_cmd(ViewportCommand::Fullscreen(false));
                ctx.send_viewport_cmd(ViewportCommand::Decorations(true));
                ctx.request_repaint_of(ViewportId::ROOT);
            }
            egui::CentralPanel::default()
                .frame(egui::Frame::NONE.fill(egui::Color32::BLACK))
                .show(ctx, |ui| {
                    if !snapshot.fullscreen {
                        ui.horizontal(|ui| {
                            ui.label(&snapshot.output.name);
                            ui.label(format!(
                                "{:.0} x {:.0}",
                                snapshot.output.size[0], snapshot.output.size[1]
                            ));
                            ui.label("Fullscreen dal pannello Output; Esc per uscire");
                        });
                    }
                    let fit = Fit::new(ui.available_rect_before_wrap(), snapshot.output.size);
                    let rect = fit.rect(snapshot.output.size);
                    render::paint(ui, rect, &snapshot.draws);
                });
            ctx.request_repaint_after(Duration::from_millis(16));
            if let Ok(mut state) = window.lock() {
                state.paints += 1;
            }
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fullscreen_reentry_always_reapplies_the_selected_monitor() {
        let ctx = egui::Context::default();
        ctx.set_embed_viewports(false);
        let mut outputs = Outputs::default();
        let output = crate::model::Project::default().outputs.remove(0);
        outputs.displays.push(Display {
            preference: MonitorPreference {
                name: "Monitor".into(),
                size: [1920, 1080],
                position: [0, 0],
            },
            scale: 1.,
            primary: true,
        });
        let mut result = ctx.run_ui(Default::default(), |ui| {
            for value in [true, false, true] {
                outputs.fullscreen(&output, value, ui.ctx());
            }
            outputs.show(ui.ctx(), &output, vec![]);
        });
        let commands = &result.viewport_output[&viewport(output.id)].commands;
        assert_eq!(
            commands
                .iter()
                .filter(|cmd| matches!(cmd, ViewportCommand::SetMonitor(0)))
                .count(),
            2
        );
        assert!(
            commands
                .iter()
                .any(|cmd| matches!(cmd, ViewportCommand::Fullscreen(false)))
        );
        result.textures_delta.clear();
    }

    #[test]
    fn fallback_converts_physical_position_using_the_departing_windows_dpi() {
        let display = Display {
            preference: MonitorPreference {
                name: "Monitor".into(),
                size: [1920, 1080],
                position: [-1920, 0],
            },
            scale: 1.,
            primary: true,
        };
        let (position, size) = fallback_window(&display, 2.);
        assert_eq!(position * 2., egui::pos2(-1880., 40.));
        assert_eq!(size * 2., egui::vec2(800., 450.));
    }
    #[test]
    fn monitor_matching_does_not_choose_arbitrary_identical_displays() {
        let p = MonitorPreference {
            name: "Display".into(),
            size: [1920, 1080],
            position: [0, 0],
        };
        let displays = [
            Display {
                preference: p.clone(),
                scale: 1.,
                primary: true,
            },
            Display {
                preference: MonitorPreference {
                    position: [1920, 0],
                    ..p.clone()
                },
                scale: 1.,
                primary: false,
            },
        ];
        assert_eq!(match_monitor(&p, &displays), Some(0));
        assert_eq!(
            match_monitor(
                &MonitorPreference {
                    position: [-1920, 0],
                    ..p
                },
                &displays
            ),
            None
        );
    }
}
