use std::sync::Arc;

use eframe::egui;
use egui::{
    Align2, Color32, FontData, FontDefinitions, FontFamily, FontId, Pos2, Rect, Response, Sense,
    Stroke, StrokeKind, Ui, Vec2,
};

pub const BG: Color32 = Color32::from_rgb(5, 9, 16);
pub const SURFACE: Color32 = Color32::from_rgb(9, 16, 28);
pub const SURFACE_2: Color32 = Color32::from_rgb(12, 22, 38);
pub const SURFACE_3: Color32 = Color32::from_rgb(16, 28, 47);
pub const BORDER: Color32 = Color32::from_rgb(25, 39, 60);
pub const BORDER_BRIGHT: Color32 = Color32::from_rgb(42, 67, 100);
pub const TEXT: Color32 = Color32::from_rgb(244, 248, 255);
pub const MUTED: Color32 = Color32::from_rgb(137, 152, 173);
pub const DIM: Color32 = Color32::from_rgb(85, 101, 124);
pub const BLUE: Color32 = Color32::from_rgb(112, 161, 255);
pub const BLUE_SOFT: Color32 = Color32::from_rgb(32, 63, 111);
pub const GREEN: Color32 = Color32::from_rgb(107, 224, 177);
pub const AMBER: Color32 = Color32::from_rgb(240, 191, 104);
pub const RED: Color32 = Color32::from_rgb(255, 119, 119);
pub const WHITE: Color32 = Color32::from_rgb(255, 255, 255);

pub fn configure(ctx: &egui::Context) {
    let mut fonts = FontDefinitions::default();
    let mut loaded_semibold = false;

    if let Ok(bytes) = std::fs::read("C:\\Windows\\Fonts\\segoeui.ttf") {
        fonts.font_data.insert(
            "segoe".to_owned(),
            Arc::new(FontData::from_owned(bytes)),
        );
        if let Some(family) = fonts.families.get_mut(&FontFamily::Proportional) {
            family.insert(0, "segoe".to_owned());
        }
    }
    if let Ok(bytes) = std::fs::read("C:\\Windows\\Fonts\\seguisb.ttf") {
        fonts.font_data.insert(
            "segoe_semibold".to_owned(),
            Arc::new(FontData::from_owned(bytes)),
        );
        loaded_semibold = true;
    }
    if let Ok(bytes) = std::fs::read("C:\\Windows\\Fonts\\consola.ttf") {
        fonts.font_data.insert(
            "consolas".to_owned(),
            Arc::new(FontData::from_owned(bytes)),
        );
        if let Some(family) = fonts.families.get_mut(&FontFamily::Monospace) {
            family.insert(0, "consolas".to_owned());
        }
    }

    let proportional = fonts
        .families
        .get(&FontFamily::Proportional)
        .cloned()
        .unwrap_or_default();
    let semibold_family = FontFamily::Name(Arc::from("semibold"));
    let mut semibold_chain = proportional;
    if loaded_semibold {
        semibold_chain.insert(0, "segoe_semibold".to_owned());
    }
    fonts.families.insert(semibold_family, semibold_chain);
    ctx.set_fonts(fonts);

    ctx.set_theme(egui::Theme::Dark);
    let mut style = (*ctx.style_of(egui::Theme::Dark)).clone();
    style.visuals = egui::Visuals::dark();
    style.visuals.panel_fill = BG;
    style.visuals.window_fill = BG;
    style.visuals.faint_bg_color = SURFACE;
    style.visuals.extreme_bg_color = SURFACE;
    style.visuals.override_text_color = Some(TEXT);
    style.visuals.selection.bg_fill = BLUE_SOFT;
    style.visuals.selection.stroke = Stroke::new(1.0, BLUE);
    style.visuals.widgets.noninteractive.bg_stroke = Stroke::new(1.0, BORDER);
    style.visuals.widgets.inactive.bg_stroke = Stroke::new(1.0, BORDER);
    style.visuals.widgets.hovered.bg_fill = SURFACE_2;
    style.visuals.widgets.hovered.bg_stroke = Stroke::new(1.0, BORDER_BRIGHT);
    style.visuals.widgets.active.bg_fill = SURFACE_3;
    style.spacing.item_spacing = Vec2::new(8.0, 8.0);
    style.spacing.button_padding = Vec2::new(12.0, 8.0);
    style.spacing.scroll.bar_width = 4.0;
    style.spacing.scroll.floating = false;
    ctx.set_style_of(egui::Theme::Dark, style);
}

pub fn font(size: f32) -> FontId {
    FontId::new(size, FontFamily::Proportional)
}

pub fn semibold(size: f32) -> FontId {
    FontId::new(size, FontFamily::Name(Arc::from("semibold")))
}

pub fn mono(size: f32) -> FontId {
    FontId::new(size, FontFamily::Monospace)
}

pub fn text(
    painter: &egui::Painter,
    pos: Pos2,
    value: impl Into<String>,
    font: FontId,
    color: Color32,
) {
    painter.text(pos, Align2::LEFT_TOP, value.into(), font, color);
}

pub fn tracked(
    painter: &egui::Painter,
    pos: Pos2,
    value: impl Into<String>,
    font: FontId,
    color: Color32,
    spacing: f32,
) {
    let mut job = egui::text::LayoutJob::default();
    job.append(
        &value.into(),
        0.0,
        egui::TextFormat {
            font_id: font,
            color,
            extra_letter_spacing: spacing,
            ..Default::default()
        },
    );
    let galley = painter.layout_job(job);
    painter.galley(pos, galley, color);
}

pub fn centered(
    painter: &egui::Painter,
    center: Pos2,
    value: impl Into<String>,
    font: FontId,
    color: Color32,
) {
    painter.text(center, Align2::CENTER_CENTER, value.into(), font, color);
}

pub fn tracked_right(
    painter: &egui::Painter,
    pos: Pos2,
    value: impl Into<String>,
    font: FontId,
    color: Color32,
    spacing: f32,
) {
    let mut job = egui::text::LayoutJob::default();
    job.append(
        &value.into(),
        0.0,
        egui::TextFormat {
            font_id: font,
            color,
            extra_letter_spacing: spacing,
            ..Default::default()
        },
    );
    let galley = painter.layout_job(job);
    let origin = Pos2::new(pos.x - galley.size().x, pos.y);
    painter.galley(origin, galley, color);
}

pub fn text_right(
    painter: &egui::Painter,
    pos: Pos2,
    value: impl Into<String>,
    font: FontId,
    color: Color32,
) {
    painter.text(pos, Align2::RIGHT_TOP, value.into(), font, color);
}

pub fn wrapped(
    ui: &mut Ui,
    rect: Rect,
    value: &str,
    font: FontId,
    color: Color32,
    line_height: f32,
) {
    let mut child = ui.new_child(
        egui::UiBuilder::new()
            .max_rect(rect)
            .layout(egui::Layout::top_down(egui::Align::LEFT)),
    );
    child.add(
        egui::Label::new(
            egui::RichText::new(value)
                .font(font)
                .color(color)
                .line_height(Some(line_height)),
        )
        .wrap(),
    );
}

pub fn panel(
    painter: &egui::Painter,
    rect: Rect,
    radius: f32,
    fill: Color32,
    border: Color32,
) {
    painter.rect_filled(rect, radius, fill);
    painter.rect_stroke(rect, radius, Stroke::new(1.0, border), StrokeKind::Inside);
}

pub fn glow(painter: &egui::Painter, center: Pos2, radius: f32, color: Color32) {
    for i in (1..=18).rev() {
        let t = i as f32 / 18.0;
        let r = radius * t;
        let alpha = (10.0 * (1.0 - t)).round() as u8;
        painter.circle_filled(
            center,
            r,
            Color32::from_rgba_unmultiplied(color.r(), color.g(), color.b(), alpha),
        );
    }
}

pub fn hairline(painter: &egui::Painter, x0: f32, x1: f32, y: f32, color: Color32) {
    painter.line_segment(
        [Pos2::new(x0, y), Pos2::new(x1, y)],
        Stroke::new(1.0, color),
    );
}

pub fn dot(painter: &egui::Painter, center: Pos2, radius: f32, color: Color32) {
    painter.circle_filled(center, radius, color);
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum ButtonKind {
    Primary,
    Secondary,
    Ghost,
    Danger,
}

pub fn button(
    ui: &mut Ui,
    rect: Rect,
    label: &str,
    kind: ButtonKind,
    enabled: bool,
) -> Response {
    let id = ui.id().with(("button", label, rect.min.x as i32, rect.min.y as i32));
    let sense = if enabled { Sense::click() } else { Sense::hover() };
    let response = ui.interact(rect, id, sense);
    let hovered = response.hovered() && enabled;
    let pressed = response.is_pointer_button_down_on() && enabled;
    let t = ui.ctx().animate_bool(response.id, hovered);
    let painter = ui.painter();

    let (fill, border, text_color) = match kind {
        ButtonKind::Primary => {
            let fill = if !enabled {
                SURFACE_2
            } else if pressed {
                Color32::from_rgb(215, 224, 238)
            } else if hovered {
                Color32::from_rgb(232, 238, 248)
            } else {
                TEXT
            };
            (fill, Color32::TRANSPARENT, BG)
        }
        ButtonKind::Secondary => {
            let fill = if pressed {
                SURFACE_3
            } else {
                Color32::from_rgba_unmultiplied(255, 255, 255, (t * 6.0) as u8)
            };
            (fill, BORDER_BRIGHT, TEXT)
        }
        ButtonKind::Ghost => {
            let fill = Color32::from_rgba_unmultiplied(255, 255, 255, (t * 8.0) as u8);
            (fill, Color32::TRANSPARENT, MUTED)
        }
        ButtonKind::Danger => {
            let fill = Color32::from_rgba_unmultiplied(255, 90, 90, 10);
            (fill, Color32::from_rgb(92, 48, 58), RED)
        }
    };

    painter.rect_filled(rect, 9.0, fill);
    if border != Color32::TRANSPARENT {
        painter.rect_stroke(rect, 9.0, Stroke::new(1.0, border), StrokeKind::Inside);
    }
    centered(
        painter,
        rect.center(),
        label,
        semibold(11.0),
        if enabled { text_color } else { DIM },
    );

    if enabled {
        response.on_hover_cursor(egui::CursorIcon::PointingHand)
    } else {
        response
    }
}

pub fn mini_button(ui: &mut Ui, rect: Rect, label: &str) -> Response {
    let id = ui.id().with(("mini", label, rect.min.x as i32, rect.min.y as i32));
    let response = ui.interact(rect, id, Sense::click());
    let hovered = response.hovered();
    let painter = ui.painter();
    if hovered {
        painter.rect_filled(rect, 7.0, SURFACE_2);
    }
    centered(
        painter,
        rect.center(),
        label,
        semibold(9.0),
        if hovered { TEXT } else { MUTED },
    );
    response.on_hover_cursor(egui::CursorIcon::PointingHand)
}

pub fn toggle(ui: &mut Ui, rect: Rect, on: bool) -> Response {
    let id = ui.id().with(("toggle", rect.min.x as i32, rect.min.y as i32));
    let response = ui.interact(rect, id, Sense::click());
    let painter = ui.painter();
    let track = Rect::from_min_size(
        Pos2::new(rect.right() - 38.0, rect.center().y - 11.0),
        Vec2::new(38.0, 22.0),
    );
    let fill = if on { BLUE_SOFT } else { SURFACE_3 };
    painter.rect_filled(track, 11.0, fill);
    painter.rect_stroke(
        track,
        11.0,
        Stroke::new(1.0, if on { BLUE } else { BORDER }),
        StrokeKind::Inside,
    );
    let knob_x = if on {
        track.right() - 11.0
    } else {
        track.left() + 11.0
    };
    painter.circle_filled(
        Pos2::new(knob_x, track.center().y),
        7.0,
        if on { TEXT } else { MUTED },
    );
    response.on_hover_cursor(egui::CursorIcon::PointingHand)
}

pub fn radio(ui: &mut Ui, rect: Rect, selected: bool) -> Response {
    let id = ui.id().with(("radio", rect.min.x as i32, rect.min.y as i32));
    let response = ui.interact(rect, id, Sense::click());
    let painter = ui.painter();
    let center = Pos2::new(rect.left() + 8.0, rect.center().y);
    painter.circle_filled(center, 7.0, if selected { BLUE } else { SURFACE_3 });
    painter.circle_stroke(
        center,
        7.0,
        Stroke::new(1.0, if selected { BLUE } else { BORDER_BRIGHT }),
    );
    if selected {
        painter.circle_filled(center, 3.0, BG);
    }
    response.on_hover_cursor(egui::CursorIcon::PointingHand)
}

pub fn close_button(ui: &mut Ui, rect: Rect) -> Response {
    let id = ui.id().with("close");
    let response = ui.interact(rect, id, Sense::click());
    let painter = ui.painter();
    if response.hovered() {
        painter.rect_filled(rect, 7.0, SURFACE_2);
    }
    let color = if response.hovered() { TEXT } else { MUTED };
    let c = rect.center();
    let s = 4.0;
    painter.line_segment(
        [Pos2::new(c.x - s, c.y - s), Pos2::new(c.x + s, c.y + s)],
        Stroke::new(1.4, color),
    );
    painter.line_segment(
        [Pos2::new(c.x + s, c.y - s), Pos2::new(c.x - s, c.y + s)],
        Stroke::new(1.4, color),
    );
    response.on_hover_cursor(egui::CursorIcon::PointingHand)
}

pub fn settings_button(ui: &mut Ui, rect: Rect) -> Response {
    let id = ui.id().with("settings");
    let response = ui.interact(rect, id, Sense::click());
    let painter = ui.painter();
    if response.hovered() {
        painter.rect_filled(rect, 7.0, SURFACE_2);
    }
    let color = if response.hovered() { TEXT } else { MUTED };
    let c = rect.center();
    for (offset, width) in [(-5.0, 8.0), (0.0, 12.0), (5.0, 7.0)] {
        let y = c.y + offset;
        painter.line_segment(
            [Pos2::new(c.x - 7.0, y), Pos2::new(c.x - 7.0 + width, y)],
            Stroke::new(1.2, color),
        );
        let knob_x = c.x - 7.0 + width - 1.0;
        painter.circle_filled(Pos2::new(knob_x, y), 1.8, color);
    }
    response.on_hover_cursor(egui::CursorIcon::PointingHand)
}

pub fn draw_brand(painter: &egui::Painter, rect: Rect) {
    painter.rect_filled(rect, 7.0, Color32::from_rgb(8, 19, 34));
    painter.rect_stroke(rect, 7.0, Stroke::new(1.0, BLUE_SOFT), StrokeKind::Inside);
    let bars = [6.0, 11.0, 8.0, 5.0];
    let start = rect.left() + 6.0;
    for (index, height) in bars.iter().enumerate() {
        let x = start + index as f32 * 3.8;
        let y0 = rect.center().y - height / 2.0;
        let y1 = rect.center().y + height / 2.0;
        painter.line_segment(
            [Pos2::new(x, y0), Pos2::new(x, y1)],
            Stroke::new(1.6, if index == 1 { BLUE } else { TEXT }),
        );
    }
}

pub fn draw_mic(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let body = Rect::from_center_size(
        Pos2::new(center.x, center.y - size * 0.12),
        Vec2::new(size * 0.34, size * 0.52),
    );
    painter.rect_stroke(body, size * 0.18, Stroke::new(1.5, color), StrokeKind::Inside);
    painter.line_segment(
        [
            Pos2::new(center.x, center.y + size * 0.18),
            Pos2::new(center.x, center.y + size * 0.35),
        ],
        Stroke::new(1.5, color),
    );
    painter.line_segment(
        [
            Pos2::new(center.x - size * 0.18, center.y + size * 0.27),
            Pos2::new(center.x + size * 0.18, center.y + size * 0.27),
        ],
        Stroke::new(1.5, color),
    );
}

pub fn draw_check(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    painter.line_segment(
        [
            Pos2::new(center.x - size * 0.32, center.y),
            Pos2::new(center.x - size * 0.08, center.y + size * 0.24),
        ],
        Stroke::new(1.7, color),
    );
    painter.line_segment(
        [
            Pos2::new(center.x - size * 0.08, center.y + size * 0.24),
            Pos2::new(center.x + size * 0.34, center.y - size * 0.24),
        ],
        Stroke::new(1.7, color),
    );
}

pub fn draw_copy(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let back = Rect::from_center_size(
        Pos2::new(center.x - size * 0.08, center.y - size * 0.08),
        Vec2::splat(size * 0.52),
    );
    let front = Rect::from_center_size(
        Pos2::new(center.x + size * 0.08, center.y + size * 0.08),
        Vec2::splat(size * 0.52),
    );
    painter.rect_stroke(back, 2.0, Stroke::new(1.2, color), StrokeKind::Inside);
    painter.rect_filled(front, 2.0, BG);
    painter.rect_stroke(front, 2.0, Stroke::new(1.2, color), StrokeKind::Inside);
}

pub fn draw_arrow(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    painter.line_segment(
        [
            Pos2::new(center.x - size * 0.32, center.y),
            Pos2::new(center.x + size * 0.32, center.y),
        ],
        Stroke::new(1.5, color),
    );
    painter.line_segment(
        [
            Pos2::new(center.x + size * 0.10, center.y - size * 0.22),
            Pos2::new(center.x + size * 0.32, center.y),
        ],
        Stroke::new(1.5, color),
    );
    painter.line_segment(
        [
            Pos2::new(center.x + size * 0.10, center.y + size * 0.22),
            Pos2::new(center.x + size * 0.32, center.y),
        ],
        Stroke::new(1.5, color),
    );
}
