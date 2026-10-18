use std::f32::consts::{PI, TAU};
use std::time::{Duration, Instant};

use eframe::egui;
use egui::{
    Align2, Color32, FontId, Pos2, Rect, Response, Sense, Stroke, StrokeKind, Ui, Vec2, pos2, vec2,
};

use crate::app::{CalibrationStage, MurmurApp, Phase, UiAction, View};
use crate::config::HotkeyChoice;
use crate::theme::{self, ButtonKind};

const MARGIN: f32 = 36.0;

impl MurmurApp {
    pub(crate) fn draw(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let painter = ui.painter().clone();
        painter.rect_filled(rect, 0.0, theme::BG);
        theme::glow(&painter, pos2(rect.left() + 120.0, rect.top() + 12.0), 460.0, theme::BLUE);

        if self.phase == Phase::Calibration || rect.height() < 300.0 {
            self.draw_calibration(ui);
        } else {
            self.draw_top_bar(ui);
            match self.view {
                View::Settings => self.draw_settings(ui),
                View::Main => match self.phase {
                    Phase::Idle => self.draw_idle(ui),
                    Phase::Setup => self.draw_setup(ui),
                    Phase::Arming => self.draw_arming(ui),
                    Phase::Listening => self.draw_listening(ui),
                    Phase::Transcribing => self.draw_transcribing(ui),
                    Phase::Cancelling => self.draw_transcribing(ui),
                    Phase::Ready => self.draw_ready(ui),
                    Phase::Error => self.draw_error(ui),
                    Phase::Calibration => {}
                },
            }
        }

        let border = Rect::from_min_max(rect.min + vec2(0.5, 0.5), rect.max - vec2(0.5, 0.5));
        let painter = ui.painter().clone();
        painter.rect_stroke(
            border,
            12.0,
            Stroke::new(1.0, theme::BORDER_BRIGHT),
            StrokeKind::Inside,
        );
    }

    fn draw_top_bar(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let w = rect.width();
        let painter = ui.painter().clone();
        theme::hairline(&painter, rect.left(), rect.right(), rect.top() + 64.0, theme::BORDER);

        let drag_rect = Rect::from_min_size(rect.min, vec2(360.0, 64.0));
        let drag = ui.interact(drag_rect, egui::Id::new("murmur-drag"), Sense::drag());
        if drag.drag_started() {
            ui.ctx()
                .send_viewport_cmd(egui::ViewportCommand::StartDrag);
        }

        theme::draw_brand(
            &painter,
            Rect::from_min_size(pos2(rect.left() + 28.0, rect.top() + 18.0), Vec2::splat(28.0)),
        );
        theme::tracked(
            &painter,
            pos2(rect.left() + 66.0, rect.top() + 18.0),
            "MURMUR.",
            theme::semibold(15.0),
            theme::TEXT,
            2.6,
        );
        theme::tracked(
            &painter,
            pos2(rect.left() + 67.0, rect.top() + 38.0),
            "DICTATION UNIT / 01",
            theme::semibold(7.5),
            theme::MUTED,
            1.2,
        );

        let status = self.status();
        self.status_pill(
            ui,
            Rect::from_min_size(
                pos2(rect.left() + w - 356.0, rect.top() + 20.0),
                vec2(140.0, 24.0),
            ),
            &status.0,
            status.1,
        );

        let hotkey_label = if self.hotkey_active {
            self.config.hotkey.compact().to_owned()
        } else {
            "NO HOTKEY".to_owned()
        };
        theme::panel(
            ui.painter(),
            Rect::from_min_size(
                pos2(rect.left() + w - 204.0, rect.top() + 20.0),
                vec2(116.0, 24.0),
            ),
            6.0,
            theme::SURFACE,
            theme::BORDER,
        );
        theme::centered(
            ui.painter(),
            pos2(rect.left() + w - 146.0, rect.top() + 32.0),
            hotkey_label,
            theme::semibold(8.0),
            if self.hotkey_active { theme::MUTED } else { theme::RED },
        );

        let settings_rect = Rect::from_min_size(
            pos2(rect.left() + w - 78.0, rect.top() + 19.0),
            Vec2::splat(26.0),
        );
        let active_session = matches!(
            self.phase,
            Phase::Arming | Phase::Listening | Phase::Transcribing | Phase::Cancelling
        );
        let settings = theme::settings_button(ui, settings_rect);
        if settings.clicked() && !active_session {
            self.action = Some(if self.view == View::Settings {
                UiAction::CloseSettings
            } else {
                UiAction::OpenSettings
            });
        }

        let close_rect = Rect::from_min_size(
            pos2(rect.left() + w - 44.0, rect.top() + 19.0),
            Vec2::splat(26.0),
        );
        let close = theme::close_button(ui, close_rect);
        if close.clicked() {
            self.action = Some(UiAction::Dismiss);
        }
    }

    fn draw_idle(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let left = rect.left() + MARGIN;
        let top = rect.top();
        let painter = ui.painter().clone();

        self.eyebrow(ui, pos2(left, top + 94.0), "MURMUR / READY", theme::BLUE);
        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(left, top + 116.0), vec2(410.0, 104.0)),
            "Tap in. Talk. Paste.",
            theme::semibold(38.0),
            theme::TEXT,
            43.0,
        );
        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(left, top + 226.0), vec2(376.0, 62.0)),
            "Claude does the listening. Murmur gets the prompt out of the way — no second speech model, no stored audio.",
            theme::font(12.5),
            theme::MUTED,
            20.0,
        );

        if theme::button(
            ui,
            Rect::from_min_size(pos2(left, top + 306.0), vec2(176.0, 44.0)),
            "START A TAKE",
            ButtonKind::Primary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::Start);
        }
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left + 190.0, top + 306.0), vec2(108.0, 44.0)),
            "SETTINGS",
            ButtonKind::Secondary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::OpenSettings);
        }

        theme::tracked(
            &painter,
            pos2(left, top + 370.0),
            "OR PRESS",
            theme::semibold(8.0),
            theme::DIM,
            1.4,
        );
        self.keycap(
            ui,
            Rect::from_min_size(pos2(left + 62.0, top + 363.0), vec2(148.0, 26.0)),
            self.config.hotkey.compact(),
            true,
        );

        let center = pos2(rect.left() + 586.0, top + 218.0);
        self.draw_dial(ui, center, 104.0);
        let found = self.claude_found;
        let (label, color) = match found {
            Some(true) if self.claude_visible => ("CLAUDE IS REACHABLE", theme::GREEN),
            Some(true) => ("OPEN CLAUDE TO CALIBRATE", theme::AMBER),
            Some(false) => ("CLAUDE IS NOT REACHABLE", theme::RED),
            None => ("CHECKING CLAUDE", theme::MUTED),
        };
        theme::dot(&painter, pos2(center.x - 66.0, top + 350.0), 3.0, color);
        theme::tracked(
            &painter,
            pos2(center.x - 56.0, top + 344.0),
            label,
            theme::semibold(8.0),
            theme::MUTED,
            1.2,
        );
        theme::tracked(
            &painter,
            pos2(center.x - 66.0, top + 371.0),
            "CLAUDE STT / ZERO AUDIO STORED",
            theme::semibold(7.0),
            theme::DIM,
            0.9,
        );

        self.draw_notice(ui);
        self.draw_footer(ui);
    }

    fn draw_setup(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let left = rect.left() + MARGIN;
        let top = rect.top();
        let painter = ui.painter().clone();

        self.eyebrow(ui, pos2(left, top + 91.0), "FIRST RUN / THREE STEPS", theme::BLUE);
        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(left, top + 113.0), vec2(400.0, 54.0)),
            "Borrow Claude's ears.",
            theme::semibold(35.0),
            theme::TEXT,
            41.0,
        );
        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(left, top + 171.0), vec2(400.0, 82.0)),
            "Murmur doesn't add another speech model. It drives the dictation already inside Claude, then puts the prompt on your clipboard.",
            theme::font(12.5),
            theme::MUTED,
            20.0,
        );

        let card = Rect::from_min_size(pos2(rect.left() + 480.0, top + 96.0), vec2(244.0, 174.0));
        theme::panel(&painter, card, 14.0, theme::SURFACE, theme::BORDER);
        theme::tracked(
            &painter,
            pos2(card.left() + 18.0, card.top() + 17.0),
            "CONNECTION / CLAUDE",
            theme::semibold(7.5),
            theme::DIM,
            1.2,
        );
        let (found_label, found_color) = match self.claude_found {
            Some(true) if self.claude_visible => ("WINDOW FOUND", theme::GREEN),
            Some(true) => ("WINDOW IN BACKGROUND", theme::AMBER),
            Some(false) => ("WINDOW NOT FOUND", theme::RED),
            None => ("CHECKING", theme::MUTED),
        };
        theme::dot(&painter, pos2(card.left() + 24.0, card.top() + 50.0), 4.0, found_color);
        theme::tracked(
            &painter,
            pos2(card.left() + 36.0, card.top() + 43.0),
            found_label,
            theme::semibold(9.0),
            theme::TEXT,
            1.1,
        );
        theme::hairline(
            &painter,
            card.left() + 18.0,
            card.right() - 18.0,
            card.top() + 68.0,
            theme::BORDER,
        );
        self.info_row(&painter, card.left() + 18.0, card.top() + 82.0, "ENGINE", "Claude STT");
        self.info_row(&painter, card.left() + 18.0, card.top() + 104.0, "AUDIO", "never stored");
        self.info_row(
            &painter,
            card.left() + 18.0,
            card.top() + 126.0,
            "SHORTCUT",
            self.config.hotkey.compact(),
        );
        theme::draw_mic(&painter, pos2(card.right() - 34.0, card.bottom() - 30.0), 26.0, theme::BLUE);

        let step_y = top + 286.0;
        let step_width = 208.0;
        for (index, (number, title, body)) in [
            (
                "01",
                "Keep Claude open",
                "The desktop app can be behind this window. It can't be fully closed.",
            ),
            (
                "02",
                "Point us at the mic",
                "Keep this card focused and hover Claude's mic and composer. Press F8. Do not click Claude.",
            ),
            (
                "03",
                "Press and speak",
                "The shortcut starts dictation. Press it again to stop, copy, and review.",
            ),
        ]
        .iter()
        .enumerate()
        {
            let x = left + index as f32 * (step_width + 18.0);
            theme::tracked(
                &painter,
                pos2(x, step_y),
                *number,
                theme::semibold(9.0),
                theme::BLUE,
                1.7,
            );
            theme::text(
                &painter,
                pos2(x, step_y + 20.0),
                *title,
                theme::semibold(12.0),
                theme::TEXT,
            );
            theme::wrapped(
                ui,
                Rect::from_min_size(pos2(x, step_y + 42.0), vec2(step_width, 58.0)),
                body,
                theme::font(10.0),
                theme::MUTED,
                15.5,
            );
        }

        let buttons_y = top + 431.0;
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left, buttons_y), vec2(198.0, 42.0)),
            "CALIBRATE CLAUDE",
            ButtonKind::Primary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::Calibrate);
        }
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left + 212.0, buttons_y), vec2(144.0, 42.0)),
            "OPEN CLAUDE",
            ButtonKind::Secondary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::OpenClaude);
        }

        if let Some(error) = &self.hotkey_error {
            theme::wrapped(
                ui,
                Rect::from_min_size(pos2(rect.right() - 320.0, buttons_y + 5.0), vec2(284.0, 32.0)),
                error,
                theme::font(9.0),
                theme::AMBER,
                13.0,
            );
        } else {
            let (hint, color) = if self.claude_found == Some(true) && !self.claude_visible {
                ("OPEN CLAUDE, THEN CALIBRATE", theme::AMBER)
            } else {
                ("CLAUDE'S DICTATION · YOUR SHORTCUT", theme::DIM)
            };
            theme::tracked(
                &painter,
                pos2(rect.right() - 320.0, buttons_y + 15.0),
                hint,
                theme::semibold(7.5),
                color,
                1.0,
            );
        }

        self.draw_notice(ui);
        self.draw_footer(ui);
    }

    fn draw_arming(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let left = rect.left() + MARGIN;
        let top = rect.top();
        let painter = ui.painter().clone();
        let pulse = 0.55 + 0.45 * ((self.phase_started.elapsed().as_secs_f32() * 4.0).sin().abs());

        self.eyebrow(ui, pos2(left, top + 96.0), "ARMING / CLAUDE", theme::BLUE);
        theme::text(
            &painter,
            pos2(left, top + 116.0),
            "Getting Claude ready.",
            theme::semibold(32.0),
            theme::TEXT,
        );
        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(left, top + 170.0), vec2(440.0, 46.0)),
            "Murmur is hiding Claude's window and reaching for the mic. This takes a moment.",
            theme::font(12.5),
            theme::MUTED,
            20.0,
        );

        let ring_center = pos2(rect.left() + 584.0, top + 226.0);
        for (index, radius) in [88.0, 68.0, 48.0].iter().enumerate() {
            painter.circle_stroke(
                ring_center,
                *radius,
                Stroke::new(
                    1.0,
                    Color32::from_rgba_unmultiplied(
                        theme::BLUE.r(),
                        theme::BLUE.g(),
                        theme::BLUE.b(),
                        (30.0 + index as f32 * 22.0) as u8,
                    ),
                ),
            );
        }
        painter.circle_filled(
            ring_center,
            34.0,
            Color32::from_rgba_unmultiplied(112, 161, 255, (pulse * 36.0) as u8),
        );
        theme::draw_mic(&painter, ring_center, 38.0, theme::TEXT);

        theme::tracked(
            &painter,
            pos2(rect.left() + 36.0, top + 444.0),
            "CLAUDE WILL BE INVISIBLE WHILE MURMUR IS OPEN",
            theme::semibold(7.5),
            theme::DIM,
            1.0,
        );
        self.draw_footer(ui);
    }

    fn draw_listening(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let left = rect.left() + MARGIN;
        let top = rect.top();
        let painter = ui.painter().clone();
        let elapsed = self
            .session_started
            .map(|started| started.elapsed())
            .unwrap_or_default();

        let pulse = 0.65 + 0.35 * ((self.phase_started.elapsed().as_secs_f32() * 3.4).sin().abs());
        painter.circle_filled(
            pos2(left + 3.0, top + 100.0),
            4.0,
            Color32::from_rgba_unmultiplied(
                theme::BLUE.r(),
                theme::BLUE.g(),
                theme::BLUE.b(),
                (120.0 + pulse * 120.0) as u8,
            ),
        );
        theme::tracked(
            &painter,
            pos2(left + 16.0, top + 94.0),
            "LISTENING",
            theme::semibold(9.0),
            theme::BLUE,
            1.8,
        );
        theme::tracked(
            &painter,
            pos2(left + 96.0, top + 94.0),
            "— CLAUDE IS CAPTURING AUDIO",
            theme::semibold(9.0),
            theme::MUTED,
            1.2,
        );

        let timer = duration_text(elapsed);
        painter.text(
            pos2(left, top + 116.0),
            Align2::LEFT_TOP,
            timer,
            theme::mono(62.0),
            theme::TEXT,
        );

        theme::tracked_right(
            &painter,
            pos2(rect.right() - 36.0, top + 102.0),
            "GHOST MODE",
            theme::semibold(7.5),
            theme::BLUE,
            1.3,
        );
        let status = if self.hotkey_active { "CLAUDE HIDDEN" } else { "HOTKEY OFF" };
        theme::text_right(
            &painter,
            pos2(rect.right() - 36.0, top + 120.0),
            status,
            theme::semibold(11.0),
            if self.hotkey_active { theme::TEXT } else { theme::RED },
        );
        theme::tracked_right(
            &painter,
            pos2(rect.right() - 36.0, top + 140.0),
            "AUDIO NEVER STORED",
            theme::semibold(7.0),
            theme::DIM,
            0.9,
        );

        let ruler_y = top + 204.0;
        for index in 0..72 {
            let x = left + index as f32 * ((rect.width() - MARGIN * 2.0) / 71.0);
            let height = if index % 12 == 0 { 10.0 } else if index % 4 == 0 { 6.0 } else { 3.0 };
            let color = if index % 12 == 0 { theme::BLUE_SOFT } else { theme::BORDER_BRIGHT };
            painter.line_segment(
                [pos2(x, ruler_y), pos2(x, ruler_y + height)],
                Stroke::new(1.0, color),
            );
        }
        theme::hairline(&painter, left, rect.right() - MARGIN, ruler_y + 13.0, theme::BORDER);

        let panel = Rect::from_min_size(
            pos2(left, top + 230.0),
            vec2(rect.width() - MARGIN * 2.0, 108.0),
        );
        theme::panel(&painter, panel, 13.0, theme::SURFACE, theme::BORDER);
        theme::tracked(
            &painter,
            pos2(panel.left() + 18.0, panel.top() + 14.0),
            "SESSION SIGNAL",
            theme::semibold(7.0),
            theme::DIM,
            1.1,
        );
        theme::tracked(
            &painter,
            pos2(panel.right() - 178.0, panel.top() + 14.0),
            "MURMUR DOES NOT RECORD",
            theme::semibold(7.0),
            theme::DIM,
            0.9,
        );

        let bars_y = panel.top() + 72.0;
        let bars_left = panel.left() + 20.0;
        let bars_right = panel.right() - 20.0;
        let bar_count = 56usize;
        let step = (bars_right - bars_left) / bar_count as f32;
        let t = self.phase_started.elapsed().as_secs_f32();
        for index in 0..bar_count {
            let phase = index as f32 * 0.72 + t * 3.2;
            let wave = (phase.sin() * 0.5 + 0.5) * 0.54
                + ((phase * 1.7 + 0.8).sin().abs()) * 0.28
                + 0.10;
            let height = (wave * 54.0).max(3.0);
            let x = bars_left + index as f32 * step;
            let color = if index % 4 == 0 {
                Color32::from_rgba_unmultiplied(
                    theme::BLUE.r(),
                    theme::BLUE.g(),
                    theme::BLUE.b(),
                    165,
                )
            } else {
                Color32::from_rgba_unmultiplied(
                    theme::TEXT.r(),
                    theme::TEXT.g(),
                    theme::TEXT.b(),
                    58,
                )
            };
            painter.rect_filled(
                Rect::from_min_max(
                    pos2(x, bars_y - height),
                    pos2(x + (step * 0.48).max(2.0), bars_y),
                ),
                1.5,
                color,
            );
        }
        theme::hairline(
            &painter,
            bars_left,
            bars_right,
            bars_y + 1.0,
            theme::BORDER_BRIGHT,
        );

        theme::text(
            &painter,
            pos2(left, top + 360.0),
            "Claude is listening.",
            theme::semibold(18.0),
            theme::TEXT,
        );
        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(left, top + 387.0), vec2(520.0, 44.0)),
            "Speak naturally. The audio stays inside Claude — Murmur only ever sees the text.",
            theme::font(11.0),
            theme::MUTED,
            17.0,
        );

        let buttons_y = top + 442.0;
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left, buttons_y), vec2(168.0, 40.0)),
            "FINISH & COPY",
            ButtonKind::Primary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::Stop);
        }
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left + 182.0, buttons_y), vec2(88.0, 40.0)),
            "CANCEL",
            ButtonKind::Ghost,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::CancelSession);
        }

        let hint = format!("{} TO FINISH", self.config.hotkey.compact());
        theme::tracked(
            &painter,
            pos2(rect.right() - MARGIN - 168.0, buttons_y + 15.0),
            hint,
            theme::semibold(7.0),
            theme::DIM,
            1.1,
        );
        self.draw_footer(ui);
    }

    fn draw_transcribing(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let left = rect.left() + MARGIN;
        let top = rect.top();
        let painter = ui.painter().clone();
        let elapsed = self.phase_started.elapsed();
        let progress = (elapsed.as_secs_f32() / 3.2).min(1.0);

        self.eyebrow(ui, pos2(left, top + 96.0), "FINALIZING / CLAUDE", theme::BLUE);
        theme::text(
            &painter,
            pos2(left, top + 116.0),
            "Turning your voice into text.",
            theme::semibold(29.0),
            theme::TEXT,
        );
        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(left, top + 158.0), vec2(540.0, 40.0)),
            "Claude is finalizing the transcript and Murmur is copying it. This usually takes a second or two.",
            theme::font(11.5),
            theme::MUTED,
            18.0,
        );

        let panel = Rect::from_min_size(
            pos2(left, top + 204.0),
            vec2(rect.width() - MARGIN * 2.0, 136.0),
        );
        theme::panel(&painter, panel, 13.0, theme::SURFACE, theme::BORDER);
        let rows = [
            ("Capture stopped", "DONE", true, false),
            ("Claude finalizing the transcript", "IN PROGRESS", false, true),
            ("Copy to clipboard", "NEXT", false, false),
        ];
        for (index, (label, state, done, active)) in rows.iter().enumerate() {
            let y = panel.top() + 24.0 + index as f32 * 36.0;
            let circle = pos2(panel.left() + 24.0, y + 7.0);
            if *done {
                painter.circle_filled(circle, 8.0, Color32::from_rgba_unmultiplied(107, 224, 177, 24));
                theme::draw_check(&painter, circle, 10.0, theme::GREEN);
            } else if *active {
                painter.circle_stroke(circle, 7.0, Stroke::new(1.0, theme::BLUE));
                painter.circle_filled(circle, 2.5, theme::BLUE);
            } else {
                painter.circle_stroke(circle, 7.0, Stroke::new(1.0, theme::BORDER_BRIGHT));
            }
            theme::text(
                &painter,
                pos2(panel.left() + 44.0, y),
                *label,
                theme::font(11.5),
                if *active { theme::TEXT } else if *done { theme::MUTED } else { theme::DIM },
            );
            theme::tracked(
                &painter,
                pos2(panel.right() - 108.0, y + 2.0),
                *state,
                theme::semibold(7.0),
                if *done {
                    theme::GREEN
                } else if *active {
                    theme::BLUE
                } else {
                    theme::DIM
                },
                1.0,
            );
        }

        let bar = Rect::from_min_size(
            pos2(left, top + 360.0),
            vec2(rect.width() - MARGIN * 2.0, 3.0),
        );
        painter.rect_filled(bar, 1.5, theme::SURFACE_3);
        let sweep_width = 120.0;
        let sweep_x = bar.left() + (progress * (bar.width() + sweep_width)) - sweep_width;
        painter.rect_filled(
            Rect::from_min_size(pos2(sweep_x.max(bar.left()), bar.top()), vec2(sweep_width.min(bar.right() - sweep_x.max(bar.left())), 3.0)),
            1.5,
            theme::BLUE,
        );

        theme::tracked(
            &painter,
            pos2(left, top + 381.0),
            &format!("TAKE TIME / {}", duration_text(elapsed)),
            theme::semibold(7.5),
            theme::DIM,
            1.1,
        );

        if theme::button(
            ui,
            Rect::from_min_size(pos2(left, top + 424.0), vec2(92.0, 36.0)),
            "CANCEL",
            ButtonKind::Ghost,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::CancelSession);
        }

        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(rect.right() - 330.0, top + 424.0), vec2(294.0, 36.0)),
            "You can close this window. Claude will still finish and the text will still be copied.",
            theme::font(9.0),
            theme::DIM,
            13.0,
        );
        self.draw_footer(ui);
    }

    fn draw_ready(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let left = rect.left() + MARGIN;
        let top = rect.top();
        let painter = ui.painter().clone();

        if self.copied {
            painter.circle_filled(
                pos2(left + 4.0, top + 101.0),
                7.0,
                Color32::from_rgba_unmultiplied(107, 224, 177, 26),
            );
            theme::draw_check(&painter, pos2(left + 4.0, top + 101.0), 9.0, theme::GREEN);
            theme::tracked(
                &painter,
                pos2(left + 20.0, top + 94.0),
                "TAKE COMPLETE / COPIED TO CLIPBOARD",
                theme::semibold(8.5),
                theme::GREEN,
                1.3,
            );
        } else {
            theme::dot(&painter, pos2(left + 4.0, top + 101.0), 4.0, theme::AMBER);
            theme::tracked(
                &painter,
                pos2(left + 20.0, top + 94.0),
                "TAKE COMPLETE / CLIPBOARD UNAVAILABLE",
                theme::semibold(8.5),
                theme::AMBER,
                1.2,
            );
        }

        theme::text(
            &painter,
            pos2(left, top + 114.0),
            "Your prompt is ready.",
            theme::semibold(29.0),
            theme::TEXT,
        );

        let meta_y = top + 158.0;
        theme::panel(
            &painter,
            Rect::from_min_size(pos2(left, meta_y), vec2(98.0, 22.0)),
            5.0,
            theme::SURFACE,
            theme::BORDER,
        );
        theme::tracked(
            &painter,
            pos2(left + 11.0, meta_y + 7.0),
            "CLAUDE STT",
            theme::semibold(7.0),
            theme::MUTED,
            1.0,
        );
        theme::tracked(
            &painter,
            pos2(left + 116.0, meta_y + 5.0),
            &format!("{} WORDS", word_count(&self.transcript)),
            theme::semibold(7.5),
            theme::MUTED,
            1.1,
        );
        theme::tracked(
            &painter,
            pos2(left + 210.0, meta_y + 5.0),
            &duration_text(self.last_duration),
            theme::semibold(7.5),
            theme::MUTED,
            1.1,
        );
        if self.config.clear_after_copy {
            theme::tracked(
                &painter,
                pos2(rect.right() - MARGIN - 112.0, meta_y + 5.0),
                "CLAUDE INPUT CLEARED",
                theme::semibold(7.0),
                theme::DIM,
                1.0,
            );
        }

        let panel = Rect::from_min_size(
            pos2(left, top + 192.0),
            vec2(rect.width() - MARGIN * 2.0, 195.0),
        );
        theme::panel(&painter, panel, 13.0, theme::SURFACE, theme::BORDER);
        self.transcript_area(ui, panel.shrink2(vec2(20.0, 17.0)), &self.transcript);

        let buttons_y = top + 408.0;
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left, buttons_y), vec2(150.0, 42.0)),
            "COPY AGAIN",
            ButtonKind::Primary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::Copy);
        }
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left + 164.0, buttons_y), vec2(118.0, 42.0)),
            "NEW TAKE",
            ButtonKind::Secondary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::NewTake);
        }
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left + 296.0, buttons_y), vec2(78.0, 42.0)),
            "CLOSE",
            ButtonKind::Ghost,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::Dismiss);
        }

        theme::tracked(
            &painter,
            pos2(rect.right() - MARGIN - 152.0, buttons_y + 16.0),
            "ENTER COPY · ESC CLOSE",
            theme::semibold(7.0),
            theme::DIM,
            0.9,
        );

        self.draw_notice(ui);
        self.draw_footer(ui);
    }

    fn draw_error(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let left = rect.left() + MARGIN;
        let top = rect.top();
        let painter = ui.painter().clone();

        theme::dot(&painter, pos2(left + 4.0, top + 101.0), 4.0, theme::RED);
        theme::tracked(
            &painter,
            pos2(left + 20.0, top + 94.0),
            if self.error_from_calibration {
                "CALIBRATION / CLAUDE"
            } else {
                "NEEDS ATTENTION / CLAUDE"
            },
            theme::semibold(8.5),
            theme::RED,
            1.3,
        );
        theme::text(
            &painter,
            pos2(left, top + 114.0),
            if self.error_from_calibration {
                "Couldn't open Claude."
            } else {
                "That take didn't land."
            },
            theme::semibold(30.0),
            theme::TEXT,
        );

        let message = self
            .error
            .clone()
            .unwrap_or_else(|| "Something interrupted the take. Try again.".to_owned());
        let panel = Rect::from_min_size(
            pos2(left, top + 174.0),
            vec2(rect.width() - MARGIN * 2.0, 106.0),
        );
        theme::panel(
            &painter,
            panel,
            13.0,
            Color32::from_rgb(17, 14, 22),
            Color32::from_rgb(76, 42, 52),
        );
        theme::wrapped(
            ui,
            panel.shrink2(vec2(20.0, 18.0)),
            &message,
            theme::font(12.0),
            Color32::from_rgb(232, 198, 204),
            19.0,
        );

        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(left, top + 298.0), vec2(600.0, 32.0)),
            "Check that Claude is open, that its composer is visible, and that the microphone is available. Murmur never changes Claude's settings.",
            theme::font(10.0),
            theme::MUTED,
            15.0,
        );

        let buttons_y = top + 352.0;
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left, buttons_y), vec2(134.0, 40.0)),
            if self.error_from_calibration {
                "CALIBRATE"
            } else {
                "TRY AGAIN"
            },
            ButtonKind::Primary,
            true,
        )
        .clicked()
        {
            self.action = Some(if self.error_from_calibration {
                UiAction::Calibrate
            } else {
                UiAction::Start
            });
        }
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left + 148.0, buttons_y), vec2(142.0, 40.0)),
            "RECALIBRATE",
            ButtonKind::Secondary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::Calibrate);
        }
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left + 304.0, buttons_y), vec2(126.0, 40.0)),
            "OPEN CLAUDE",
            ButtonKind::Ghost,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::OpenClaude);
        }
        if theme::mini_button(
            ui,
            Rect::from_min_size(pos2(left + 444.0, buttons_y + 4.0), vec2(76.0, 32.0)),
            "SETTINGS",
        )
        .clicked()
        {
            self.action = Some(UiAction::OpenSettings);
        }

        self.draw_notice(ui);
        self.draw_footer(ui);
    }

    fn draw_settings(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let left = rect.left() + MARGIN;
        let top = rect.top();
        let painter = ui.painter().clone();

        theme::tracked(
            &painter,
            pos2(left, top + 90.0),
            "SETTINGS / MURMUR",
            theme::semibold(8.0),
            theme::BLUE,
            1.6,
        );
        theme::text(
            &painter,
            pos2(left, top + 110.0),
            "Set it up your way.",
            theme::semibold(27.0),
            theme::TEXT,
        );
        if theme::mini_button(
            ui,
            Rect::from_min_size(pos2(rect.right() - MARGIN - 64.0, top + 92.0), vec2(64.0, 26.0)),
            "BACK",
        )
        .clicked()
        {
            self.action = Some(UiAction::CloseSettings);
        }

        let left_x = left;
        let right_x = rect.left() + 458.0;
        let section = |painter: &egui::Painter, x: f32, y: f32, label: &str| {
            theme::tracked(
                &painter,
                pos2(x, y),
                label,
                theme::semibold(7.5),
                theme::DIM,
                1.4,
            );
        };

        section(&painter, left_x, top + 158.0, "SHORTCUT");
        self.shortcut_option(
            ui,
            Rect::from_min_size(pos2(left_x, top + 176.0), vec2(405.0, 30.0)),
            HotkeyChoice::CtrlAltSpace,
            "Ctrl + Alt + Space",
            "Recommended. Won't collide with most apps.",
        );
        self.shortcut_option(
            ui,
            Rect::from_min_size(pos2(left_x, top + 210.0), vec2(405.0, 30.0)),
            HotkeyChoice::CtrlSpace,
            "Ctrl + Space",
            "Compact. Windows IME may claim it.",
        );
        if let Some(error) = &self.hotkey_error {
            theme::wrapped(
                ui,
                Rect::from_min_size(pos2(left_x, top + 244.0), vec2(405.0, 26.0)),
                error,
                theme::font(9.0),
                theme::AMBER,
                12.0,
            );
        }

        section(&painter, left_x, top + 278.0, "CALIBRATION");
        self.calibration_row(
            &painter,
            pos2(left_x, top + 296.0),
            "MICROPHONE POINT",
            self.config.mic.map(|point| point.display()).unwrap_or_else(|| "not set".to_owned()),
        );
        self.calibration_row(
            &painter,
            pos2(left_x, top + 318.0),
            "COMPOSER POINT",
            self.config.focus.map(|point| point.display()).unwrap_or_else(|| "not set".to_owned()),
        );
        if theme::button(
            ui,
            Rect::from_min_size(pos2(left_x, top + 344.0), vec2(142.0, 30.0)),
            "RECALIBRATE",
            ButtonKind::Secondary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::Calibrate);
        }

        section(&painter, left_x, top + 388.0, "BEHAVIOR");
        self.toggle_row(
            ui,
            Rect::from_min_size(pos2(left_x, top + 406.0), vec2(405.0, 24.0)),
            "Clear Claude input after copy",
            self.config.clear_after_copy,
            UiAction::ToggleClear,
        );
        self.toggle_row(
            ui,
            Rect::from_min_size(pos2(left_x, top + 434.0), vec2(405.0, 24.0)),
            "Minimize Claude after every take",
            self.config.minimize_after,
            UiAction::ToggleMinimize,
        );
        self.toggle_row(
            ui,
            Rect::from_min_size(pos2(left_x, top + 462.0), vec2(405.0, 24.0)),
            "Start Murmur with Windows",
            self.config.launch_at_login,
            UiAction::ToggleAutostart,
        );

        section(&painter, right_x, top + 158.0, "STATUS");
        let card = Rect::from_min_size(pos2(right_x, top + 176.0), vec2(rect.right() - MARGIN - right_x, 126.0));
        theme::panel(&painter, card, 12.0, theme::SURFACE, theme::BORDER);
        let (claude_label, claude_color) = match self.claude_found {
            Some(true) if self.claude_visible => ("ready", theme::GREEN),
            Some(true) if self.claude_minimized => ("minimized", theme::AMBER),
            Some(true) => ("background", theme::AMBER),
            Some(false) => ("not found", theme::RED),
            None => ("checking", theme::MUTED),
        };
        self.status_row(&painter, card.left() + 16.0, card.top() + 18.0, "Claude window", claude_label, claude_color);
        self.status_row(
            &painter,
            card.left() + 16.0,
            card.top() + 42.0,
            "Shortcut",
            if self.hotkey_active { "registered" } else { "off" },
            if self.hotkey_active { theme::GREEN } else { theme::RED },
        );
        self.status_row(
            &painter,
            card.left() + 16.0,
            card.top() + 66.0,
            "Bridge",
            "native",
            theme::BLUE,
        );
        self.status_row(
            &painter,
            card.left() + 16.0,
            card.top() + 90.0,
            "Version",
            env!("CARGO_PKG_VERSION"),
            theme::MUTED,
        );

        section(&painter, right_x, top + 320.0, "RECOVERY");
        if theme::button(
            ui,
            Rect::from_min_size(pos2(right_x, top + 338.0), vec2(122.0, 30.0)),
            "OPEN LOGS",
            ButtonKind::Secondary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::OpenLogs);
        }
        if theme::button(
            ui,
            Rect::from_min_size(pos2(right_x + 132.0, top + 338.0), vec2(134.0, 30.0)),
            "RESTORE CLAUDE",
            ButtonKind::Secondary,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::RestoreClaude);
        }

        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(right_x, top + 384.0), vec2(rect.right() - MARGIN - right_x, 58.0)),
            "Murmur uses Claude's own dictation. It never records, stores, or transcribes audio itself.",
            theme::font(9.0),
            theme::DIM,
            14.0,
        );

        if theme::button(
            ui,
            Rect::from_min_size(pos2(right_x, top + 444.0), vec2(122.0, 30.0)),
            "QUIT MURMUR",
            ButtonKind::Danger,
            true,
        )
        .clicked()
        {
            self.action = Some(UiAction::Quit);
        }

        self.draw_notice(ui);
        self.draw_footer(ui);
    }

    fn draw_calibration(&mut self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let painter = ui.painter().clone();
        let w = rect.width();
        let Some(calibration) = self.calibration.as_ref() else {
            return;
        };
        theme::glow(&painter, pos2(rect.left() + 90.0, rect.top() + 10.0), 240.0, theme::BLUE);

        theme::tracked(
            &painter,
            pos2(rect.left() + 20.0, rect.top() + 16.0),
            "CALIBRATION",
            theme::semibold(8.0),
            theme::BLUE,
            1.5,
        );
        theme::tracked(
            &painter,
            pos2(rect.right() - 170.0, rect.top() + 16.0),
            if calibration.stage == CalibrationStage::Mic {
                "STEP 1 OF 2"
            } else {
                "STEP 2 OF 2"
            },
            theme::semibold(7.5),
            theme::DIM,
            1.2,
        );
        if theme::close_button(
            ui,
            Rect::from_min_size(pos2(rect.right() - 38.0, rect.top() + 12.0), Vec2::splat(22.0)),
        )
        .clicked()
        {
            self.action = Some(UiAction::CancelCalibration);
        }

        let (heading, body) = match calibration.stage {
            CalibrationStage::Mic => (
                "Hover the mic icon.",
                "Move your cursor over Claude's microphone button, then press F8.",
            ),
            CalibrationStage::Focus => (
                "Hover the text box.",
                "Now point to the composer where your prompt appears, then press F8.",
            ),
            CalibrationStage::Preparing => (
                "Finding Claude…",
                "Murmur is reading Claude's window. This only takes a moment.",
            ),
        };
        theme::text(
            &painter,
            pos2(rect.left() + 20.0, rect.top() + 42.0),
            heading,
            theme::semibold(23.0),
            theme::TEXT,
        );
        theme::wrapped(
            ui,
            Rect::from_min_size(pos2(rect.left() + 20.0, rect.top() + 79.0), vec2(w - 40.0, 34.0)),
            body,
            theme::font(11.0),
            theme::MUTED,
            16.0,
        );

        theme::panel(
            &painter,
            Rect::from_min_size(pos2(rect.left() + 20.0, rect.top() + 121.0), vec2(46.0, 24.0)),
            5.0,
            theme::SURFACE,
            theme::BORDER_BRIGHT,
        );
        theme::centered(
            &painter,
            pos2(rect.left() + 43.0, rect.top() + 133.0),
            "F8",
            theme::mono(10.0),
            theme::TEXT,
        );
        theme::tracked(
            &painter,
            pos2(rect.left() + 76.0, rect.top() + 126.0),
            "CAPTURE POINT",
            theme::semibold(7.5),
            theme::MUTED,
            1.2,
        );

        let step_count = 2;
        let current = if calibration.stage == CalibrationStage::Mic { 0 } else { 1 };
        for index in 0..step_count {
            let center = pos2(rect.right() - 38.0 - (step_count - 1 - index) as f32 * 13.0, rect.top() + 133.0);
            if index == current {
                painter.circle_filled(center, 3.5, theme::BLUE);
            } else {
                painter.circle_stroke(center, 3.0, Stroke::new(1.0, theme::BORDER_BRIGHT));
            }
        }

        let message = calibration
            .message
            .clone()
            .unwrap_or_else(|| "Keep this card focused. Do not click Claude — just hover the point.".to_owned());
        theme::text(
            &painter,
            pos2(rect.left() + 20.0, rect.top() + 153.0),
            message,
            theme::font(8.5),
            if calibration.message.is_some() { theme::AMBER } else { theme::DIM },
        );

        let border = Rect::from_min_max(rect.min + vec2(0.5, 0.5), rect.max - vec2(0.5, 0.5));
        painter.rect_stroke(
            border,
            12.0,
            Stroke::new(1.0, theme::BORDER_BRIGHT),
            StrokeKind::Inside,
        );
    }

    fn draw_dial(&self, ui: &mut Ui, center: Pos2, radius: f32) {
        let painter = ui.painter().clone();
        painter.circle_stroke(center, radius, Stroke::new(1.0, theme::BORDER));
        painter.circle_stroke(center, radius - 16.0, Stroke::new(1.0, theme::BORDER));
        painter.circle_filled(center, radius - 26.0, theme::SURFACE);
        painter.circle_stroke(
            center,
            radius - 26.0,
            Stroke::new(1.0, theme::BORDER_BRIGHT),
        );

        for index in 0..48 {
            let angle = index as f32 / 48.0 * TAU - PI / 2.0;
            let major = index % 4 == 0;
            let inner = radius - if major { 11.0 } else { 8.0 };
            let outer = radius - 3.0;
            let color = if major { theme::BLUE_SOFT } else { theme::BORDER };
            painter.line_segment(
                [
                    pos2(center.x + angle.cos() * inner, center.y + angle.sin() * inner),
                    pos2(center.x + angle.cos() * outer, center.y + angle.sin() * outer),
                ],
                Stroke::new(1.0, color),
            );
        }

        // Two quiet arcs give the dial its instrument feel without becoming neon.
        draw_arc(&painter, center, radius - 40.0, -2.55, -1.45, theme::BLUE, 34);
        draw_arc(&painter, center, radius - 40.0, -0.55, 0.55, theme::BLUE, 30);

        theme::draw_mic(&painter, center, 44.0, theme::TEXT);
        theme::tracked(
            &painter,
            pos2(center.x - 23.0, center.y + 40.0),
            "READY",
            theme::semibold(7.0),
            theme::MUTED,
            1.4,
        );
    }

    fn draw_notice(&self, ui: &mut Ui) {
        let Some(notice) = &self.notice else {
            return;
        };
        let rect = ui.max_rect();
        let painter = ui.painter().clone();
        theme::draw_check(
            &painter,
            pos2(rect.left() + MARGIN + 6.0, rect.bottom() - 51.0),
            10.0,
            theme::GREEN,
        );
        theme::text(
            &painter,
            pos2(rect.left() + MARGIN + 20.0, rect.bottom() - 57.0),
            notice,
            theme::font(9.5),
            theme::MUTED,
        );
    }

    fn draw_footer(&self, ui: &mut Ui) {
        let rect = ui.max_rect();
        let painter = ui.painter().clone();
        let y = rect.bottom() - 27.0;
        theme::hairline(&painter, rect.left(), rect.right(), y, theme::BORDER);
        theme::tracked(
            &painter,
            pos2(rect.left() + MARGIN, y + 10.0),
            "CLAUDE LISTENS. YOU PASTE.",
            theme::semibold(7.0),
            theme::DIM,
            1.1,
        );
        theme::tracked(
            &painter,
            pos2(rect.right() - MARGIN - 62.0, y + 10.0),
            &format!("MURMUR {}", env!("CARGO_PKG_VERSION")),
            theme::semibold(6.5),
            theme::DIM,
            0.8,
        );
    }

    fn eyebrow(&self, ui: &mut Ui, pos: Pos2, text: &str, color: Color32) {
        let painter = ui.painter().clone();
        theme::dot(&painter, pos2(pos.x + 3.0, pos.y + 5.0), 3.0, color);
        theme::tracked(
            &painter,
            pos2(pos.x + 14.0, pos.y),
            text,
            theme::semibold(8.0),
            color,
            1.5,
        );
    }

    fn status(&self) -> (String, Color32) {
        match self.phase {
            Phase::Listening => ("CLAUDE / LISTENING".into(), theme::BLUE),
            Phase::Arming => ("CLAUDE / ARMING".into(), theme::BLUE),
            Phase::Transcribing | Phase::Cancelling => ("CLAUDE / WRITING".into(), theme::BLUE),
            Phase::Calibration => ("CLAUDE / CALIBRATING".into(), theme::AMBER),
            _ => match self.claude_found {
                Some(true) if self.claude_visible => ("CLAUDE / READY".into(), theme::GREEN),
                Some(true) if self.claude_minimized => ("CLAUDE / MINIMIZED".into(), theme::AMBER),
                Some(true) => ("CLAUDE / BACKGROUND".into(), theme::AMBER),
                Some(false) => ("CLAUDE / OFFLINE".into(), theme::RED),
                None => ("CLAUDE / CHECKING".into(), theme::MUTED),
            },
        }
    }

    fn status_pill(&self, ui: &mut Ui, rect: Rect, label: &str, color: Color32) {
        let painter = ui.painter().clone();
        theme::panel(&painter, rect, 6.0, theme::SURFACE, theme::BORDER);
        theme::dot(&painter, pos2(rect.left() + 12.0, rect.center().y), 3.0, color);
        theme::tracked(
            &painter,
            pos2(rect.left() + 22.0, rect.top() + 8.0),
            label,
            theme::semibold(7.2),
            theme::MUTED,
            1.0,
        );
    }

    fn keycap(&self, ui: &mut Ui, rect: Rect, label: &str, active: bool) {
        theme::panel(
            ui.painter(),
            rect,
            6.0,
            theme::SURFACE,
            if active { theme::BORDER_BRIGHT } else { theme::BORDER },
        );
        theme::centered(
            ui.painter(),
            rect.center(),
            label,
            theme::mono(9.0),
            if active { theme::TEXT } else { theme::DIM },
        );
    }

    fn info_row(&self, painter: &egui::Painter, x: f32, y: f32, label: &str, value: &str) {
        theme::tracked(
            &painter,
            pos2(x, y),
            label,
            theme::semibold(7.0),
            theme::DIM,
            1.0,
        );
        painter.text(
            pos2(x + 98.0, y - 2.0),
            Align2::LEFT_TOP,
            value,
            theme::mono(8.0),
            theme::MUTED,
        );
    }

    fn status_row(
        &self,
        painter: &egui::Painter,
        x: f32,
        y: f32,
        label: &str,
        value: &str,
        color: Color32,
    ) {
        theme::text(&painter, pos2(x, y), label, theme::font(9.5), theme::MUTED);
        theme::dot(&painter, pos2(x + 116.0, y + 5.0), 2.5, color);
        theme::text(&painter, pos2(x + 124.0, y), value, theme::font(9.5), theme::TEXT);
    }

    fn calibration_row(&self, painter: &egui::Painter, pos: Pos2, label: &str, value: String) {
        theme::tracked(
            &painter,
            pos,
            label,
            theme::semibold(6.8),
            theme::DIM,
            0.9,
        );
        painter.text(
            pos2(pos.x + 150.0, pos.y - 2.0),
            Align2::LEFT_TOP,
            value,
            theme::mono(8.0),
            theme::MUTED,
        );
    }

    fn shortcut_option(
        &mut self,
        ui: &mut Ui,
        rect: Rect,
        choice: HotkeyChoice,
        title: &str,
        detail: &str,
    ) {
        let id = ui
            .scope_id()
            .with(("shortcut", rect.min.x as i32, rect.min.y as i32));
        let response = ui.interact(rect, id, Sense::click());
        let painter = ui.painter().clone();
        let selected = self.config.hotkey == choice;
        if selected {
            painter.rect_filled(rect, 7.0, theme::SURFACE_2);
            painter.rect_stroke(
                rect,
                7.0,
                Stroke::new(1.0, theme::BLUE_SOFT),
                StrokeKind::Inside,
            );
        } else if response.hovered() {
            painter.rect_filled(rect, 7.0, theme::SURFACE);
        }
        let ring = pos2(rect.left() + 13.0, rect.center().y);
        painter.circle_stroke(
            ring,
            6.0,
            Stroke::new(1.0, if selected { theme::BLUE } else { theme::BORDER_BRIGHT }),
        );
        if selected {
            painter.circle_filled(ring, 2.5, theme::BLUE);
        }
        painter.text(
            pos2(rect.left() + 28.0, rect.top() + 3.0),
            Align2::LEFT_TOP,
            title,
            theme::semibold(9.5),
            theme::TEXT,
        );
        painter.text(
            pos2(rect.left() + 28.0, rect.top() + 16.0),
            Align2::LEFT_TOP,
            detail,
            theme::font(8.0),
            theme::MUTED,
        );
        if selected {
            theme::tracked(
                &painter,
                pos2(rect.right() - 46.0, rect.top() + 10.0),
                "ACTIVE",
                theme::semibold(6.5),
                theme::BLUE,
                0.9,
            );
        }
        if response.clicked() {
            self.action = Some(UiAction::SetHotkey(choice));
        }
        response.on_hover_cursor(egui::CursorIcon::PointingHand);
    }

    fn toggle_row(&mut self, ui: &mut Ui, rect: Rect, label: &str, value: bool, action: UiAction) {
        let id = ui
            .scope_id()
            .with(("toggle-row", rect.min.x as i32, rect.min.y as i32));
        let response = ui.interact(rect, id, Sense::click());
        if response.hovered() {
            ui.painter().rect_filled(rect, 6.0, theme::SURFACE);
        }
        ui.painter().text(
            pos2(rect.left() + 2.0, rect.top() + 4.0),
            Align2::LEFT_TOP,
            label,
            theme::font(9.5),
            theme::MUTED,
        );
        let switch = theme::toggle(
            ui,
            Rect::from_min_size(pos2(rect.right() - 38.0, rect.top()), vec2(38.0, 22.0)),
            value,
        );
        if response.clicked() || switch.clicked() {
            self.action = Some(action);
        }
    }

    fn transcript_area(&self, ui: &mut Ui, rect: Rect, text: &str) {
        let mut child = ui.new_child(
            egui::UiBuilder::new()
                .max_rect(rect)
                .layout(egui::Layout::top_down(egui::Align::LEFT)),
        );
        child.set_clip_rect(rect);
        egui::ScrollArea::vertical()
            .auto_shrink([false, false])
            .show(&mut child, |ui| {
                ui.add(
                    egui::Label::new(
                        egui::RichText::new(text)
                            .font(theme::font(14.0))
                            .color(theme::TEXT)
                            .line_height(Some(24.0)),
                    )
                    .wrap(),
                );
            });
    }
}

fn draw_arc(
    painter: &egui::Painter,
    center: Pos2,
    radius: f32,
    start: f32,
    end: f32,
    color: Color32,
    segments: usize,
) {
    let mut previous = None;
    for index in 0..=segments {
        let t = index as f32 / segments as f32;
        let angle = start + (end - start) * t;
        let point = pos2(
            center.x + radius * angle.cos(),
            center.y + radius * angle.sin(),
        );
        if let Some(previous) = previous {
            painter.line_segment([previous, point], Stroke::new(1.4, color));
        }
        previous = Some(point);
    }
}

fn duration_text(duration: Duration) -> String {
    let seconds = duration.as_secs_f32();
    let minutes = (seconds / 60.0).floor() as u64;
    let remaining = seconds - minutes as f32 * 60.0;
    format!("{minutes:02}:{remaining:04.1}")
}

fn word_count(text: &str) -> usize {
    text.split_whitespace().count()
}

#[allow(dead_code)]
fn _assert_time(_: Instant) {}
