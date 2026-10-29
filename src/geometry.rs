use serde::{Deserialize, Serialize};

pub type Point = [f64; 2];

fn cross(a: Point, b: Point, c: Point) -> f64 {
    (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
}

pub fn bounds(points: &[Point]) -> [f64; 4] {
    points.iter().fold(
        [
            f64::INFINITY,
            f64::INFINITY,
            f64::NEG_INFINITY,
            f64::NEG_INFINITY,
        ],
        |[x0, y0, x1, y1], [x, y]| [x0.min(*x), y0.min(*y), x1.max(*x), y1.max(*y)],
    )
}

pub fn contains(points: &[Point], p: Point) -> bool {
    let mut inside = false;
    for (a, b) in points
        .iter()
        .zip(points.iter().cycle().skip(1))
        .take(points.len())
    {
        if (a[1] > p[1]) != (b[1] > p[1])
            && p[0] < (b[0] - a[0]) * (p[1] - a[1]) / (b[1] - a[1]) + a[0]
        {
            inside = !inside;
        }
    }
    inside
}

pub fn valid_polygon(points: &[Point]) -> bool {
    if points.len() < 3
        || points.len() > 4096
        || !points
            .iter()
            .flatten()
            .all(|v| v.is_finite() && v.abs() <= 1e7)
    {
        return false;
    }
    let [x0, y0, x1, y1] = bounds(points);
    let epsilon = ((x1 - x0) * (y1 - y0)).max(1.0) * 1e-8;
    let area: f64 = points
        .iter()
        .zip(points.iter().cycle().skip(1))
        .take(points.len())
        .map(|(a, b)| a[0] * b[1] - b[0] * a[1])
        .sum();
    if area.abs() < epsilon {
        return false;
    }
    for i in 0..points.len() {
        for j in i + 1..points.len() {
            if j == i + 1 || (i == 0 && j == points.len() - 1) {
                continue;
            }
            let (a, b, c, d) = (
                points[i],
                points[(i + 1) % points.len()],
                points[j],
                points[(j + 1) % points.len()],
            );
            let overlaps = a[0].min(b[0]) <= c[0].max(d[0])
                && c[0].min(d[0]) <= a[0].max(b[0])
                && a[1].min(b[1]) <= c[1].max(d[1])
                && c[1].min(d[1]) <= a[1].max(b[1]);
            if overlaps
                && cross(a, b, c) * cross(a, b, d) <= 0.0
                && cross(c, d, a) * cross(c, d, b) <= 0.0
            {
                return false;
            }
        }
    }
    if points.len() == 4 {
        let sign = cross(points[0], points[1], points[2]).signum();
        if !(0..4)
            .all(|i| cross(points[i], points[(i + 1) % 4], points[(i + 2) % 4]) * sign > epsilon)
        {
            return false;
        }
        return homography(points).is_some();
    }
    true
}

/// Maps destination coordinates to homogeneous texture coordinates.
pub fn homography(points: &[Point]) -> Option<[[f64; 3]; 3]> {
    if points.len() != 4 {
        return None;
    }
    let uv = [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]];
    let [x0, y0, x1, y1] = bounds(points);
    let scale = (x1 - x0).max(y1 - y0);
    if scale <= 1e-9 {
        return None;
    }
    let mut a = [[0.0; 9]; 8];
    for (i, ([x, y], [u, v])) in points.iter().copied().zip(uv).enumerate() {
        let (x, y) = ((x - x0) / scale, (y - y0) / scale);
        a[2 * i] = [x, y, 1.0, 0.0, 0.0, 0.0, -u * x, -u * y, u];
        a[2 * i + 1] = [0.0, 0.0, 0.0, x, y, 1.0, -v * x, -v * y, v];
    }
    for col in 0..8 {
        let pivot = (col..8).max_by(|i, j| a[*i][col].abs().total_cmp(&a[*j][col].abs()))?;
        if a[pivot][col].abs() < 1e-10 {
            return None;
        }
        a.swap(pivot, col);
        let divisor = a[col][col];
        for value in &mut a[col][col..] {
            *value /= divisor;
        }
        let row = a[col];
        for (i, values) in a.iter_mut().enumerate() {
            if i != col {
                let factor = values[col];
                for j in col..9 {
                    values[j] -= factor * row[j];
                }
            }
        }
    }
    let h = [
        [
            a[0][8] / scale,
            a[1][8] / scale,
            a[2][8] - (a[0][8] * x0 + a[1][8] * y0) / scale,
        ],
        [
            a[3][8] / scale,
            a[4][8] / scale,
            a[5][8] - (a[3][8] * x0 + a[4][8] * y0) / scale,
        ],
        [
            a[6][8] / scale,
            a[7][8] / scale,
            1.0 - (a[6][8] * x0 + a[7][8] * y0) / scale,
        ],
    ];
    let q: Vec<_> = points.iter().map(|p| transform(h, *p)[2]).collect();
    if !h.iter().flatten().all(|v| v.is_finite())
        || q.iter()
            .any(|v| v.abs() < 1e-8 || v.signum() != q[0].signum())
    {
        return None;
    }
    Some(h)
}

pub fn transform(h: [[f64; 3]; 3], [x, y]: Point) -> [f64; 3] {
    h.map(|row| row[0] * x + row[1] * y + row[2])
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
pub struct Fit {
    pub origin: [f32; 2],
    pub scale: f32,
}

impl Fit {
    pub fn new(rect: eframe::egui::Rect, size: [f64; 2]) -> Self {
        let scale = (rect.width() / size[0] as f32)
            .min(rect.height() / size[1] as f32)
            .max(0.0001);
        Self {
            origin: [
                rect.center().x - size[0] as f32 * scale / 2.0,
                rect.center().y - size[1] as f32 * scale / 2.0,
            ],
            scale,
        }
    }
    pub fn screen(self, p: Point) -> eframe::egui::Pos2 {
        eframe::egui::pos2(
            self.origin[0] + p[0] as f32 * self.scale,
            self.origin[1] + p[1] as f32 * self.scale,
        )
    }
    pub fn project(self, p: eframe::egui::Pos2) -> Point {
        [
            ((p.x - self.origin[0]) / self.scale) as f64,
            ((p.y - self.origin[1]) / self.scale) as f64,
        ]
    }
    pub fn rect(self, size: [f64; 2]) -> eframe::egui::Rect {
        eframe::egui::Rect::from_min_size(
            self.screen([0.0, 0.0]),
            eframe::egui::vec2(size[0] as f32 * self.scale, size[1] as f32 * self.scale),
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn perspective_maps_diagonal_intersection_to_texture_center() {
        let p = [[0.0, 0.0], [4.0, 0.0], [3.0, 2.0], [1.0, 2.0]];
        let h = homography(&p).expect("homography");
        let q = transform(h, [2.0, 4.0 / 3.0]);
        assert!((q[0] / q[2] - 0.5).abs() < 1e-8 && (q[1] / q[2] - 0.5).abs() < 1e-8);
    }
    #[test]
    fn rejects_concave_crossed_and_degenerate_quads() {
        for p in [
            [[0., 0.], [2., 2.], [2., 0.], [0., 2.]],
            [[0., 0.], [2., 0.], [0.5, 0.5], [0., 2.]],
            [[0., 0.], [1., 0.], [2., 0.], [3., 0.]],
        ] {
            assert!(!valid_polygon(&p));
        }
    }
    #[test]
    fn fit_preserves_coordinates_and_letterboxes() {
        use eframe::egui::{Rect, pos2, vec2};
        let fit = Fit::new(
            Rect::from_min_size(pos2(20., 40.), vec2(400., 400.)),
            [1920., 1080.],
        );
        let p = [960., 540.];
        assert_eq!(fit.project(fit.screen(p)), p);
        assert!(fit.rect([1920., 1080.]).height() < 400.);
    }
}
