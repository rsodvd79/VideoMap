use crate::{
    geometry::{Point, bounds, homography, transform},
    model::Polygon,
};
use anyhow::{Context, Result, ensure};
use bytemuck::{Pod, Zeroable};
use eframe::{
    egui,
    egui_wgpu::{
        self,
        wgpu::{self, util::DeviceExt},
    },
};
use std::{collections::HashMap, sync::Arc};
use uuid::Uuid;

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Vertex {
    position: [f32; 2],
    uvq: [f32; 3],
}

struct Image {
    texture: wgpu::Texture,
    binding: Arc<wgpu::BindGroup>,
    size: [u32; 2],
    bgra: bool,
}

struct Geometry {
    points: Vec<Point>,
    vertices: Arc<wgpu::Buffer>,
    count: u32,
}

#[derive(Clone)]
pub struct Draw {
    pipeline: Arc<wgpu::RenderPipeline>,
    binding: Arc<wgpu::BindGroup>,
    vertices: Arc<wgpu::Buffer>,
    count: u32,
}

impl egui_wgpu::CallbackTrait for Draw {
    fn paint(
        &self,
        _info: egui::PaintCallbackInfo,
        pass: &mut wgpu::RenderPass<'static>,
        _: &egui_wgpu::CallbackResources,
    ) {
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, self.binding.as_ref(), &[]);
        pass.set_vertex_buffer(0, self.vertices.slice(..));
        pass.draw(0..self.count, 0..1);
    }
}

pub struct Renderer {
    device: wgpu::Device,
    queue: wgpu::Queue,
    pipeline: Arc<wgpu::RenderPipeline>,
    layout: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    images: HashMap<Uuid, Image>,
    geometry: HashMap<(Uuid, [u64; 2]), Geometry>,
}

impl Renderer {
    pub fn retain_surfaces(&mut self, sizes: impl Iterator<Item = [f64; 2]>) {
        let active: Vec<_> = sizes.map(|size| size.map(f64::to_bits)).collect();
        self.geometry.retain(|(_, size), _| active.contains(size));
    }

    pub fn new(state: &egui_wgpu::RenderState) -> Self {
        Self::with_gpu(
            state.device.clone(),
            state.queue.clone(),
            state.target_format,
        )
    }

    fn with_gpu(device: wgpu::Device, queue: wgpu::Queue, target: wgpu::TextureFormat) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("VideoMap prospettiva"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/mapping.wgsl").into()),
        });
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("VideoMap texture"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("VideoMap"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("VideoMap mapping"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: std::mem::size_of::<Vertex>() as u64,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &wgpu::vertex_attr_array![0 => Float32x2,1 => Float32x3],
                })],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: target,
                    blend: Some(wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                cull_mode: None,
                ..Default::default()
            },
            depth_stencil: None,
            multisample: Default::default(),
            multiview_mask: None,
            cache: None,
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("VideoMap sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        Self {
            device,
            queue,
            pipeline: Arc::new(pipeline),
            layout,
            sampler,
            images: HashMap::new(),
            geometry: HashMap::new(),
        }
    }

    pub fn upload(&mut self, id: Uuid, size: [u32; 2], pixels: &[u8], bgra: bool) -> Result<()> {
        let [w, h] = size;
        let limit = self.device.limits().max_texture_dimension_2d;
        ensure!(
            w > 0 && h > 0 && w <= limit && h <= limit,
            "Dimensioni media oltre il limite GPU ({limit})"
        );
        ensure!(
            pixels.len() == w as usize * h as usize * 4,
            "Frame media incompleto"
        );
        if self
            .images
            .get(&id)
            .is_none_or(|image| image.size != size || image.bgra != bgra)
        {
            let texture = self.device.create_texture(&wgpu::TextureDescriptor {
                label: Some("VideoMap media"),
                size: wgpu::Extent3d {
                    width: w,
                    height: h,
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: if bgra {
                    wgpu::TextureFormat::Bgra8UnormSrgb
                } else {
                    wgpu::TextureFormat::Rgba8UnormSrgb
                },
                usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                view_formats: &[],
            });
            let view = texture.create_view(&Default::default());
            let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("VideoMap media"),
                layout: &self.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(&view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::Sampler(&self.sampler),
                    },
                ],
            });
            self.images.insert(
                id,
                Image {
                    texture,
                    binding: Arc::new(binding),
                    size,
                    bgra,
                },
            );
        }
        let image = self.images.get(&id).context("Texture non disponibile")?;
        self.queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &image.texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            pixels,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(w * 4),
                rows_per_image: Some(h),
            },
            wgpu::Extent3d {
                width: w,
                height: h,
                depth_or_array_layers: 1,
            },
        );
        Ok(())
    }

    pub fn remove(&mut self, id: Uuid) {
        self.images.remove(&id);
        self.geometry.retain(|(polygon, _), _| *polygon != id);
    }

    pub fn clear(&mut self) {
        self.images.clear();
        self.geometry.clear();
    }

    pub fn draw(&mut self, polygon: &Polygon, size: [f64; 2]) -> Option<Draw> {
        let image = self.images.get(&polygon.id)?;
        let key = (polygon.id, size.map(f64::to_bits));
        if self
            .geometry
            .get(&key)
            .is_none_or(|g| g.points != polygon.points)
        {
            let points = &polygon.points;
            let flat: Vec<_> = points.iter().flatten().copied().collect();
            let indices = if points.len() == 4 {
                vec![0, 1, 2, 0, 2, 3]
            } else {
                earcutr::earcut(&flat, &[], 2).ok()?
            };
            let h = homography(points);
            let [x0, y0, x1, y1] = bounds(points);
            let vertices: Vec<_> = indices
                .iter()
                .map(|i| {
                    let p = points[*i];
                    let uvq = if let Some(h) = h {
                        transform(h, p)
                    } else {
                        [(p[0] - x0) / (x1 - x0), (p[1] - y0) / (y1 - y0), 1.]
                    };
                    Vertex {
                        position: [
                            (p[0] / size[0] * 2. - 1.) as f32,
                            (1. - p[1] / size[1] * 2.) as f32,
                        ],
                        uvq: uvq.map(|v| v as f32),
                    }
                })
                .collect();
            if vertices.is_empty() {
                return None;
            }
            let buffer = self
                .device
                .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some("VideoMap poligono"),
                    contents: bytemuck::cast_slice(&vertices),
                    usage: wgpu::BufferUsages::VERTEX,
                });
            self.geometry.insert(
                key,
                Geometry {
                    points: points.clone(),
                    vertices: Arc::new(buffer),
                    count: vertices.len() as u32,
                },
            );
        }
        let geometry = self.geometry.get(&key)?;
        Some(Draw {
            pipeline: self.pipeline.clone(),
            binding: image.binding.clone(),
            vertices: geometry.vertices.clone(),
            count: geometry.count,
        })
    }
}

pub fn paint(ui: &egui::Ui, rect: egui::Rect, draws: &[Draw]) {
    ui.painter().rect_filled(rect, 0., egui::Color32::BLACK);
    for draw in draws {
        ui.painter()
            .add(egui_wgpu::Callback::new_paint_callback(rect, draw.clone()));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore = "Richiede un adattatore GPU nativo"]
    fn gpu_uses_perspective_not_affine_uvs() -> Result<()> {
        let instance = wgpu::Instance::default();
        let adapter = pollster::block_on(instance.request_adapter(&Default::default()))?;
        let (device, queue) = pollster::block_on(adapter.request_device(&Default::default()))?;
        let format = wgpu::TextureFormat::Rgba8UnormSrgb;
        let mut renderer = Renderer::with_gpu(device.clone(), queue.clone(), format);
        let mut polygon = Polygon::square("Griglia".into(), [256., 256.]);
        polygon.points = vec![[0., 0.], [256., 0.], [192., 256.], [64., 256.]];
        let mut pixels = vec![];
        for y in 0..64 {
            for x in 0..64 {
                pixels.extend_from_slice(&[(x * 255 / 63) as u8, (y * 255 / 63) as u8, 0, 255]);
            }
        }
        renderer.upload(polygon.id, [64, 64], &pixels, false)?;
        let draw = renderer
            .draw(&polygon, [256., 256.])
            .context("Geometria GPU")?;
        let target = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Test prospettiva"),
            size: wgpu::Extent3d {
                width: 256,
                height: 256,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let readback = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Readback"),
            size: 256 * 256 * 4,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        let view = target.create_view(&Default::default());
        let mut encoder = device.create_command_encoder(&Default::default());
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("Test prospettiva"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::BLACK),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            pass.set_pipeline(&draw.pipeline);
            pass.set_bind_group(0, draw.binding.as_ref(), &[]);
            pass.set_vertex_buffer(0, draw.vertices.slice(..));
            pass.draw(0..draw.count, 0..1);
        }
        encoder.copy_texture_to_buffer(
            wgpu::TexelCopyTextureInfo {
                texture: &target,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyBufferInfo {
                buffer: &readback,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(1024),
                    rows_per_image: Some(256),
                },
            },
            wgpu::Extent3d {
                width: 256,
                height: 256,
                depth_or_array_layers: 1,
            },
        );
        queue.submit([encoder.finish()]);
        let (tx, rx) = std::sync::mpsc::channel();
        readback.slice(..).map_async(wgpu::MapMode::Read, move |r| {
            let _ = tx.send(r);
        });
        let _ = instance.poll_all(true);
        rx.recv_timeout(std::time::Duration::from_secs(10))??;
        let bytes = readback.slice(..).get_mapped_range()?;
        let center = (170 * 256 + 128) * 4;
        assert!(
            (i32::from(bytes[center]) - 128).abs() < 4
                && (i32::from(bytes[center + 1]) - 128).abs() < 4,
            "UV prospettiche al centro: {:?}",
            &bytes[center..center + 4]
        );
        assert_eq!(&bytes[(255 * 256) * 4..(255 * 256) * 4 + 3], &[0, 0, 0]);
        drop(bytes);
        readback.unmap();
        renderer
            .draw(&polygon, [512., 512.])
            .context("Ridimensionamento")?;
        renderer.retain_surfaces(std::iter::once([256., 256.]));
        assert_eq!(renderer.geometry.len(), 1);
        Ok(())
    }
}
