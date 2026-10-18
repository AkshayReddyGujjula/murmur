use std::sync::mpsc::{self, Receiver, Sender};
use std::thread;
use std::time::{Duration, Instant};

use anyhow::{Result, anyhow, bail};
use eframe::egui;
use windows::Win32::Foundation::HWND;

use crate::config::{Config, HotkeyChoice, Point};
use crate::helper::{HelperSession, WindowInfo};
use crate::platform::{self, HotkeyService};
use crate::theme;
use crate::tray::{self, TrayAction, TrayState};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Phase {
    Idle,
    Setup,
    Arming,
    Listening,
    Transcribing,
    Cancelling,
    Ready,
    Error,
    Calibration,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum View {
    Main,
    Settings,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CalibrationStage {
    Preparing,
    Mic,
    Focus,
}

#[derive(Debug, Clone)]
pub struct CalibrationState {
    pub stage: CalibrationStage,
    pub window: Option<WindowInfo>,
    pub previous: isize,
    pub mic: Option<Point>,
    pub focus: Option<Point>,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Copy)]
pub enum UiAction {
    Start,
    Stop,
    CancelSession,
    Dismiss,
    Copy,
    NewTake,
    OpenSettings,
    CloseSettings,
    Calibrate,
    CancelCalibration,
    SetHotkey(HotkeyChoice),
    ToggleClear,
    ToggleMinimize,
    ToggleAutostart,
    OpenClaude,
    OpenLogs,
    RestoreClaude,
    Quit,
}

#[derive(Debug, Clone)]
enum SessionEvent {
    Started,
    Transcribing,
    Finished(Option<String>),
    Cancelled,
    Failed(String),
}

#[derive(Debug, Clone, Copy)]
enum SessionCommand {
    Stop,
    Cancel,
}

#[derive(Debug)]
enum AppEvent {
    Hotkey,
    F8,
    Tray(TrayAction),
    Session { id: u64, event: SessionEvent },
    CalibrationReady { window: WindowInfo, previous: isize },
    CalibrationFailed(String),
    ClaudeStatus {
        found: bool,
        visible: bool,
        minimized: bool,
    },
    Restored,
}

pub struct MurmurApp {
    pub(crate) config: Config,
    pub(crate) phase: Phase,
    pub(crate) view: View,
    pub(crate) transcript: String,
    pub(crate) error: Option<String>,
    pub(crate) copied: bool,
    pub(crate) notice: Option<String>,
    pub(crate) claude_found: Option<bool>,
    pub(crate) claude_visible: bool,
    pub(crate) claude_minimized: bool,
    pub(crate) error_from_calibration: bool,
    pub(crate) hotkey_active: bool,
    pub(crate) hotkey_error: Option<String>,
    pub(crate) session_started: Option<Instant>,
    pub(crate) last_duration: Duration,
    pub(crate) last_words: usize,
    pub(crate) calibration: Option<CalibrationState>,
    pub(crate) window_visible: bool,
    pub(crate) phase_started: Instant,
    pub(crate) action: Option<UiAction>,

    hwnd: Option<HWND>,
    show_on_launch: bool,
    first_frame: bool,
    tray_initialized: bool,
    tray: Option<TrayState>,
    tray_rx: Receiver<TrayAction>,
    events_tx: Sender<AppEvent>,
    events_rx: Receiver<AppEvent>,
    session_tx: Option<(u64, Sender<SessionCommand>)>,
    session_id: u64,
    stop_deadline: Option<Instant>,
    cancel_then_hide: bool,
    hotkey: Option<HotkeyService>,
    f8_hotkey: Option<HotkeyService>,
    quit_requested: bool,
}

impl MurmurApp {
    pub fn new(
        cc: &eframe::CreationContext<'_>,
        config: Config,
        start_hidden: bool,
    ) -> Self {
        theme::configure(&cc.egui_ctx);
        let (events_tx, events_rx) = mpsc::channel();
        let (_tray_placeholder_tx, tray_rx) = mpsc::channel();

        let mut config = config;
        let mut hotkey_error = None;
        let hotkey = match register_hotkey(config.hotkey, events_tx.clone(), cc.egui_ctx.clone()) {
            Ok(service) => Some(service),
            Err(error) => {
                if config.hotkey == HotkeyChoice::CtrlSpace {
                    match register_hotkey(
                        HotkeyChoice::CtrlAltSpace,
                        events_tx.clone(),
                        cc.egui_ctx.clone(),
                    ) {
                        Ok(service) => {
                            config.hotkey = HotkeyChoice::CtrlAltSpace;
                            let _ = config.save();
                            hotkey_error = Some(format!(
                                "Ctrl + Space is unavailable ({error}). Using Ctrl + Alt + Space."
                            ));
                            Some(service)
                        }
                        Err(fallback_error) => {
                            hotkey_error = Some(format!(
                                "Could not register a shortcut: {fallback_error}"
                            ));
                            None
                        }
                    }
                } else {
                    hotkey_error = Some(format!("Could not register the shortcut: {error}"));
                    None
                }
            }
        };

        let calibrated = config.is_calibrated();
        let show_on_launch = !start_hidden || !calibrated;
        let phase = if calibrated { Phase::Idle } else { Phase::Setup };
        let hotkey_active = hotkey.is_some();

        let now = Instant::now();
        let mut app = Self {
            config,
            phase,
            view: View::Main,
            transcript: String::new(),
            error: None,
            copied: false,
            notice: None,
            claude_found: None,
            claude_visible: false,
            claude_minimized: false,
            error_from_calibration: false,
            hotkey_active,
            hotkey_error,
            session_started: None,
            last_duration: Duration::ZERO,
            last_words: 0,
            calibration: None,
            window_visible: false,
            phase_started: now,
            action: None,
            hwnd: None,
            show_on_launch,
            first_frame: true,
            tray_initialized: false,
            tray: None,
            tray_rx,
            events_tx,
            events_rx,
            session_tx: None,
            session_id: 0,
            stop_deadline: None,
            cancel_then_hide: false,
            hotkey,
            f8_hotkey: None,
            quit_requested: false,
        };
        app.apply_preview();
        app
    }

    fn apply_preview(&mut self) {
        let Ok(mode) = std::env::var("MURMUR_PREVIEW") else {
            return;
        };
        self.show_on_launch = true;
        self.claude_found = Some(true);
        self.claude_visible = true;
        self.claude_minimized = false;
        self.hotkey_active = true;
        self.hotkey_error = None;
        let now = Instant::now();
        match mode.as_str() {
            "setup" => self.phase = Phase::Setup,
            "idle" => self.phase = Phase::Idle,
            "listening" => {
                self.phase = Phase::Listening;
                self.phase_started = now - Duration::from_millis(12_400);
                self.session_started = Some(now - Duration::from_millis(12_400));
            }
            "transcribing" => {
                self.phase = Phase::Transcribing;
                self.phase_started = now - Duration::from_millis(1_250);
                self.session_started = Some(now - Duration::from_millis(15_000));
            }
            "ready" => {
                self.phase = Phase::Ready;
                self.transcript = "Refactor the auth middleware to use the new token validation helper. Then update the test matrix, add a regression case for expired refresh tokens, and explain the migration in one short paragraph for the changelog.".to_owned();
                self.copied = true;
                self.last_duration = Duration::from_millis(12_400);
                self.last_words = word_count(&self.transcript);
                self.notice = Some("Copied to clipboard.".to_owned());
            }
            "error" => {
                self.phase = Phase::Error;
                self.error = Some("Claude's window was not found. Open Claude, make sure the main window is running, then try again.".to_owned());
            }
            "settings" => {
                self.phase = Phase::Idle;
                self.view = View::Settings;
            }
            "calibration" => {
                self.phase = Phase::Calibration;
                self.calibration = Some(CalibrationState {
                    stage: CalibrationStage::Mic,
                    window: None,
                    previous: 0,
                    mic: None,
                    focus: None,
                    message: None,
                });
            }
            _ => {}
        }
    }

    fn set_phase(&mut self, phase: Phase) {
        self.phase = phase;
        self.phase_started = Instant::now();
    }

    fn on_logic(&mut self, ctx: &egui::Context) {
        if self.first_frame {
            self.first_frame = false;
            self.hwnd = platform::find_own_window();
            if let Some(hwnd) = self.hwnd {
                platform::prepare_overlay(hwnd);
            }

            if !self.tray_initialized {
                self.tray_initialized = true;
                let (tray_tx, tray_rx) = mpsc::channel();
                self.tray_rx = tray_rx;
                match tray::create(ctx.clone(), tray_tx, self.config.hotkey.label()) {
                    Ok(tray) => self.tray = Some(tray),
                    Err(error) => {
                        crate::config::log_line(&format!("tray unavailable: {error}"));
                    }
                }
            }

            self.spawn_claude_check(ctx.clone());

            if self.show_on_launch {
                self.show_window(ctx, true);
            }
        }

        while let Ok(event) = self.events_rx.try_recv() {
            self.handle_event(event, ctx);
        }
        while let Ok(action) = self.tray_rx.try_recv() {
            self.handle_tray(action, ctx);
        }

        if let Some(deadline) = self.stop_deadline {
            if Instant::now() > deadline {
                if let Some((_, tx)) = self.session_tx.take() {
                    let _ = tx.send(SessionCommand::Cancel);
                }
                self.error = Some(
                    "Claude took too long to finish. The take was cancelled and Claude was restored."
                        .to_owned(),
                );
                self.set_phase(Phase::Error);
                self.show_window(ctx, true);
                self.stop_deadline = None;
            }
        }

        match self.phase {
            Phase::Arming => ctx.request_repaint_after(Duration::from_millis(80)),
            Phase::Listening => ctx.request_repaint_after(Duration::from_millis(33)),
            Phase::Transcribing | Phase::Cancelling => {
                ctx.request_repaint_after(Duration::from_millis(80));
            }
            Phase::Calibration => ctx.request_repaint_after(Duration::from_millis(100)),
            _ => {}
        }

        if self.quit_requested {
            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        }
    }

    fn handle_tray(&mut self, action: TrayAction, ctx: &egui::Context) {
        match action {
            TrayAction::Toggle => {
                if self.window_visible {
                    if self.phase == Phase::Listening {
                        self.stop_session(ctx);
                    } else if self.phase == Phase::Calibration {
                        self.cancel_calibration(ctx);
                    } else {
                        self.hide_window(ctx);
                    }
                } else {
                    if self.phase == Phase::Setup || self.phase == Phase::Idle {
                        self.show_window(ctx, true);
                    } else if self.phase == Phase::Ready || self.phase == Phase::Error {
                        self.show_window(ctx, true);
                    } else if self.phase == Phase::Calibration {
                        self.show_window(ctx, false);
                    } else {
                        self.show_window(ctx, true);
                    }
                }
            }
            TrayAction::Start => {
                if self.phase != Phase::Listening && self.phase != Phase::Transcribing {
                    self.start_session(ctx);
                }
            }
            TrayAction::Settings => {
                self.view = View::Settings;
                self.show_window(ctx, true);
            }
            TrayAction::Quit => {
                self.request_quit(ctx);
            }
        }
    }

    fn handle_event(&mut self, event: AppEvent, ctx: &egui::Context) {
        match event {
            AppEvent::Hotkey => {
                if self.phase == Phase::Calibration {
                    return;
                }
                if self.view == View::Settings {
                    self.view = View::Main;
                }
                match self.phase {
                    Phase::Listening => self.stop_session(ctx),
                    Phase::Arming | Phase::Transcribing | Phase::Cancelling => {}
                    Phase::Setup => {
                        self.show_window(ctx, true);
                    }
                    _ => self.start_session(ctx),
                }
            }
            AppEvent::F8 => {
                crate::config::log_line(&format!(
                    "F8 event phase={:?} stage={:?}",
                    self.phase,
                    self.calibration.as_ref().map(|c| c.stage)
                ));
                if self.phase == Phase::Calibration {
                    self.capture_calibration(ctx);
                }
            }
            AppEvent::Tray(action) => self.handle_tray(action, ctx),
            AppEvent::Session { id, event } => {
                if id != self.session_id || self.session_tx.is_none() {
                    return;
                }
                match event {
                    SessionEvent::Started => {
                        if self.phase == Phase::Cancelling {
                            if let Some((_, tx)) = &self.session_tx {
                                let _ = tx.send(SessionCommand::Cancel);
                            }
                            return;
                        }
                        self.session_started = Some(Instant::now());
                        self.set_phase(Phase::Listening);
                        self.show_window(ctx, false);
                    }
                    SessionEvent::Transcribing => {
                        if self.phase == Phase::Listening || self.phase == Phase::Arming {
                            self.set_phase(Phase::Transcribing);
                            self.stop_deadline = Some(Instant::now() + Duration::from_secs(25));
                        }
                    }
                    SessionEvent::Finished(text) => {
                        self.session_tx = None;
                        self.stop_deadline = None;
                        let elapsed = self
                            .session_started
                            .take()
                            .map(|start| start.elapsed())
                            .unwrap_or_default();
                        self.last_duration = elapsed;
                        match text {
                            Some(text) if !text.trim().is_empty() => {
                                self.transcript = text;
                                self.last_words = word_count(&self.transcript);
                                self.copied = set_clipboard(&self.transcript);
                                self.error = None;
                                self.config.last_take_words = self.last_words as u32;
                                self.config.last_take_ms = elapsed.as_millis() as u64;
                                let _ = self.config.save();
                                self.set_phase(Phase::Ready);
                                self.show_window(ctx, true);
                            }
                            _ => {
                                self.error = Some(
                                    "Claude didn't return any text. Check the calibration points, then try again."
                                        .to_owned(),
                                );
                                self.set_phase(Phase::Error);
                                self.show_window(ctx, true);
                            }
                        }
                    }
                    SessionEvent::Cancelled => {
                        self.session_tx = None;
                        self.stop_deadline = None;
                        if self.cancel_then_hide {
                            self.cancel_then_hide = false;
                            self.hide_window(ctx);
                            self.set_phase(Phase::Idle);
                        } else {
                            self.set_phase(Phase::Idle);
                            self.show_window(ctx, true);
                        }
                    }
                    SessionEvent::Failed(message) => {
                        self.session_tx = None;
                        self.stop_deadline = None;
                        self.error = Some(message);
                        self.error_from_calibration = false;
                        self.set_phase(Phase::Error);
                        self.show_window(ctx, true);
                    }
                }
            }
            AppEvent::CalibrationReady { window, previous } => {
                self.calibration = Some(CalibrationState {
                    stage: CalibrationStage::Mic,
                    window: Some(window),
                    previous,
                    mic: None,
                    focus: None,
                    message: None,
                });
                self.set_phase(Phase::Calibration);
                let ppp = ctx.pixels_per_point();
                if let Some(hwnd) = self.hwnd {
                    platform::resize_window(
                        hwnd,
                        (520.0 * ppp) as i32,
                        (178.0 * ppp) as i32,
                        (28.0 * ppp) as i32,
                    );
                }
                self.show_window(ctx, true);
                if let Some(hwnd) = self.hwnd {
                    platform::set_vertical_position(hwnd, (28.0 * ppp) as i32);
                }
                self.start_f8(ctx);
            }
            AppEvent::CalibrationFailed(message) => {
                self.hotkey_error = None;
                self.error = Some(message);
                self.error_from_calibration = true;
                self.calibration = None;
                self.set_phase(Phase::Error);
                self.show_window(ctx, true);
            }
            AppEvent::ClaudeStatus {
                found,
                visible,
                minimized,
            } => {
                self.claude_found = Some(found);
                self.claude_visible = visible;
                self.claude_minimized = minimized;
            }
            AppEvent::Restored => {
                self.notice = Some("Claude's window state was restored.".to_owned());
            }
        }
    }

    fn spawn_claude_check(&self, ctx: egui::Context) {
        let tx = self.events_tx.clone();
        thread::spawn(move || {
            let status = match HelperSession::start().and_then(|mut helper| helper.find()) {
                Ok(Some(window)) => (true, window.visible, window.minimized),
                _ => (false, false, false),
            };
            let _ = tx.send(AppEvent::ClaudeStatus {
                found: status.0,
                visible: status.1,
                minimized: status.2,
            });
            ctx.request_repaint();
        });
    }

    fn start_session(&mut self, ctx: &egui::Context) {
        if self.phase == Phase::Listening {
            self.stop_session(ctx);
            return;
        }
        if matches!(
            self.phase,
            Phase::Arming | Phase::Transcribing | Phase::Cancelling | Phase::Calibration
        ) {
            return;
        }
        if self.view == View::Settings {
            self.view = View::Main;
        }
        let (Some(mic), Some(focus)) = (self.config.mic, self.config.focus) else {
            self.set_phase(Phase::Setup);
            self.show_window(ctx, true);
            return;
        };

        self.session_id = self.session_id.wrapping_add(1);
        let id = self.session_id;
        let (command_tx, command_rx) = mpsc::channel();
        self.session_tx = Some((id, command_tx));
        self.session_started = None;
        self.stop_deadline = None;
        self.cancel_then_hide = false;
        self.transcript.clear();
        self.error = None;
        self.copied = false;
        self.notice = None;
        self.set_phase(Phase::Arming);
        self.view = View::Main;
        self.show_window(ctx, false);

        let events = self.events_tx.clone();
        let ctx_clone = ctx.clone();
        let clear_after = self.config.clear_after_copy;
        let minimize_after = self.config.minimize_after;
        thread::spawn(move || {
            let mut helper = match HelperSession::start() {
                Ok(helper) => helper,
                Err(error) => {
                    let _ = events.send(AppEvent::Session {
                        id,
                        event: SessionEvent::Failed(format!(
                            "Could not start the Claude bridge: {error}"
                        )),
                    });
                    ctx_clone.request_repaint();
                    return;
                }
            };

            let mut recording = false;
            let mut cancelled = false;
            let outcome = (|| -> Result<Option<String>> {
                let window = helper
                    .find()?
                    .ok_or_else(|| anyhow!("Claude's window was not found. Open Claude and try again."))?;
                if !window.is_usable() {
                    bail!("Claude's window is too small to calibrate against. Maximize it and try again.");
                }
                helper.begin(mic, focus, "alpha", false, minimize_after)?;
                recording = true;
                let _ = events.send(AppEvent::Session {
                    id,
                    event: SessionEvent::Started,
                });
                ctx_clone.request_repaint();
                if let Err(error) = helper.click_through(true) {
                    crate::config::log_line(&format!("click-through failed: {error}"));
                }

                match command_rx.recv_timeout(Duration::from_secs(300)) {
                    Ok(SessionCommand::Stop) => {
                        let _ = helper.click_through(false);
                        recording = false;
                        let _ = events.send(AppEvent::Session {
                            id,
                            event: SessionEvent::Transcribing,
                        });
                        ctx_clone.request_repaint();
                        let text = helper.end(mic, clear_after)?;
                        Ok(text)
                    }
                    Ok(SessionCommand::Cancel) | Err(_) => {
                        cancelled = true;
                        let _ = helper.cancel();
                        recording = false;
                        Ok(None)
                    }
                }
            })();

            if outcome.is_err() && recording {
                let _ = helper.cancel();
            }
            helper.quit();
            match outcome {
                Ok(_) if cancelled => {
                    let _ = events.send(AppEvent::Session {
                        id,
                        event: SessionEvent::Cancelled,
                    });
                }
                Ok(text) => {
                    let _ = events.send(AppEvent::Session {
                        id,
                        event: SessionEvent::Finished(text),
                    });
                }
                Err(error) => {
                    let _ = events.send(AppEvent::Session {
                        id,
                        event: SessionEvent::Failed(error.to_string()),
                    });
                }
            }
            ctx_clone.request_repaint();
        });
    }

    fn stop_session(&mut self, ctx: &egui::Context) {
        if self.phase != Phase::Listening {
            return;
        }
        if let Some((_, tx)) = &self.session_tx {
            let _ = tx.send(SessionCommand::Stop);
            self.set_phase(Phase::Transcribing);
            self.stop_deadline = Some(Instant::now() + Duration::from_secs(25));
            ctx.request_repaint();
        }
    }

    fn cancel_session(&mut self, ctx: &egui::Context, hide_after: bool) {
        if let Some((_, tx)) = &self.session_tx {
            let _ = tx.send(SessionCommand::Cancel);
            self.cancel_then_hide = hide_after;
            self.set_phase(Phase::Cancelling);
            self.stop_deadline = None;
            ctx.request_repaint();
        } else {
            self.set_phase(Phase::Idle);
            if hide_after {
                self.hide_window(ctx);
            }
        }
    }

    fn show_window(&mut self, ctx: &egui::Context, focus: bool) {
        if self.hwnd.is_none() {
            self.hwnd = platform::find_own_window();
        }
        let Some(hwnd) = self.hwnd else {
            return;
        };
        platform::prepare_overlay(hwnd);
        let ppp = ctx.pixels_per_point();
        let calibration_mode = self.phase == Phase::Calibration;
        if calibration_mode {
            platform::resize_window(
                hwnd,
                (520.0 * ppp) as i32,
                (178.0 * ppp) as i32,
                (28.0 * ppp) as i32,
            );
        } else {
            platform::resize_window(
                hwnd,
                (760.0 * ppp) as i32,
                (520.0 * ppp) as i32,
                (0.0 * ppp) as i32,
            );
        }
        // Keep eframe's viewport state in sync with the native window. Showing only
        // through Win32 leaves egui thinking the window is hidden, so it never paints.
        ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
        platform::show_overlay(hwnd, focus);
        if focus {
            platform::focus_overlay(hwnd);
            ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
        } else {
            platform::set_no_activate(hwnd, true);
        }
        if calibration_mode {
            platform::set_vertical_position(hwnd, (28.0 * ppp) as i32);
        }
        self.window_visible = true;
        ctx.request_repaint();
    }

    fn hide_window(&mut self, ctx: &egui::Context) {
        ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));
        if let Some(hwnd) = self.hwnd {
            platform::hide_overlay(hwnd);
        }
        self.window_visible = false;
        ctx.request_repaint();
    }

    fn start_calibration(&mut self, ctx: &egui::Context) {
        if self.phase == Phase::Listening {
            self.cancel_session(ctx, false);
        }
        // Read-only calibration: do not hide/activate anything. The card is
        // small, topmost and no-activate; the user can click Claude behind it
        // and hover the two points. This avoids both the restore-state write
        // and Windows' foreground-lock refusal.
        self.calibration = Some(CalibrationState {
            stage: CalibrationStage::Preparing,
            window: None,
            previous: 0,
            mic: None,
            focus: None,
            message: None,
        });
        self.set_phase(Phase::Calibration);
        self.view = View::Main;
        self.error = None;

        let tx = self.events_tx.clone();
        let ctx_clone = ctx.clone();
        thread::spawn(move || {
            let result = (|| -> Result<(WindowInfo, isize)> {
                let mut helper = HelperSession::start()?;
                let window = helper
                    .find()?
                    .ok_or_else(|| anyhow!("Claude's window was not found. Open Claude and try again."))?;
                if !window.visible {
                    bail!("Claude is running in the background. Open it from the taskbar, then calibrate.");
                }
                helper.quit();
                Ok((window, 0))
            })();
            match result {
                Ok((window, previous)) => {
                    let _ = tx.send(AppEvent::CalibrationReady { window, previous });
                }
                Err(error) => {
                    let _ = tx.send(AppEvent::CalibrationFailed(format!(
                        "Could not read Claude's window: {error}"
                    )));
                }
            }
            ctx_clone.request_repaint();
        });
    }

    fn start_f8(&mut self, ctx: &egui::Context) {
        self.f8_hotkey = None;
        let previous = self.calibration.as_ref().map(|cal| cal.previous).unwrap_or(0);
        let tx = self.events_tx.clone();
        let ctx_clone = ctx.clone();
        match HotkeyService::start_f8(move || {
            let _ = tx.send(AppEvent::F8);
            ctx_clone.request_repaint();
        }) {
            Ok(service) => {
                crate::config::log_line("F8 hotkey registered");
                self.f8_hotkey = Some(service);
            }
            Err(error) => {
                self.calibration = None;
                self.restore_previous(ctx, previous);
                self.error = Some(format!(
                    "F8 is already in use, so calibration cannot start: {error}"
                ));
                self.set_phase(Phase::Error);
                self.show_window(ctx, true);
            }
        }
    }

    fn capture_calibration(&mut self, ctx: &egui::Context) {
        let Some(calibration) = self.calibration.as_mut() else {
            return;
        };
        let Some(window) = calibration.window.as_ref() else {
            return;
        };
        let (cx, cy) = platform::cursor_position();
        let client = &window.client;
        let nx = (cx - client.x) as f64 / client.w.max(1) as f64;
        let ny = (cy - client.y) as f64 / client.h.max(1) as f64;
        crate::config::log_line(&format!(
            "calibration capture stage={:?} cursor={},{} client={},{},{},{} norm={:.4},{:.4}",
            calibration.stage, cx, cy, client.x, client.y, client.w, client.h, nx, ny
        ));
        let Some(point) = Point::new(nx, ny) else {
            calibration.message = Some("That point is outside Claude. Try again.".to_owned());
            crate::config::log_line("calibration rejected: outside");
            ctx.request_repaint();
            return;
        };
        if nx < 0.01 || nx > 0.99 || ny < 0.01 || ny > 0.99 {
            calibration.message = Some("That point is on the edge. Aim a little inside.".to_owned());
            crate::config::log_line("calibration rejected: edge");
            ctx.request_repaint();
            return;
        }

        match calibration.stage {
            CalibrationStage::Mic => {
                calibration.mic = Some(point);
                calibration.stage = CalibrationStage::Focus;
                calibration.message = Some("Mic captured. Now hover Claude's text box.".to_owned());
            }
            CalibrationStage::Focus => {
                calibration.focus = Some(point);
                let mic = calibration.mic;
                let focus = calibration.focus;
                let previous = calibration.previous;
                if let (Some(mic), Some(focus)) = (mic, focus) {
                    self.config.mic = Some(mic);
                    self.config.focus = Some(focus);
                    self.config.first_run_done = true;
                    let _ = self.config.save();
                    self.calibration = None;
                    self.f8_hotkey = None;
                    self.set_phase(Phase::Idle);
                    self.notice =
                        Some("Calibration saved. Press the hotkey and start talking.".to_owned());
                    self.restore_previous(ctx, previous);
                    self.show_window(ctx, false);
                }
            }
            CalibrationStage::Preparing => {}
        }
        ctx.request_repaint();
    }

    fn cancel_calibration(&mut self, ctx: &egui::Context) {
        let previous = self.calibration.as_ref().map(|cal| cal.previous).unwrap_or(0);
        self.calibration = None;
        self.f8_hotkey = None;
        self.set_phase(if self.config.is_calibrated() {
            Phase::Idle
        } else {
            Phase::Setup
        });
        self.restore_previous(ctx, previous);
        self.show_window(ctx, false);
    }

    fn restore_previous(&self, _ctx: &egui::Context, previous: isize) {
        if previous == 0 {
            return;
        }
        let own = self.hwnd.map(|hwnd| hwnd.0 as isize).unwrap_or(0);
        let restore_focus = previous != 0 && previous != own;
        // Run this synchronously so the calibration overlay gets focus *after*
        // Claude has been put back. Asynchronous restore lets the previous
        // foreground window steal focus back from Murmur.
        let _ = thread::spawn(move || {
            if let Ok(mut helper) = HelperSession::start() {
                // Consumes the calibration restore record and returns Claude to
                // its previous minimized/visible state.
                let _ = helper.restore();
                if restore_focus {
                    let _ = helper.activate(previous);
                }
                helper.quit();
            }
        })
        .join();
    }

    fn request_quit(&mut self, ctx: &egui::Context) {
        if let Some((_, tx)) = self.session_tx.take() {
            let _ = tx.send(SessionCommand::Cancel);
        }
        self.quit_requested = true;
        ctx.send_viewport_cmd(egui::ViewportCommand::Close);
    }

    fn handle_action(&mut self, action: UiAction, ctx: &egui::Context) {
        match action {
            UiAction::Start | UiAction::NewTake => self.start_session(ctx),
            UiAction::Stop => self.stop_session(ctx),
            UiAction::CancelSession => self.cancel_session(ctx, true),
            UiAction::Dismiss => {
                if self.phase == Phase::Listening || self.phase == Phase::Arming || self.phase == Phase::Transcribing {
                    self.cancel_session(ctx, true);
                } else if self.phase == Phase::Calibration {
                    self.cancel_calibration(ctx);
                } else if self.view == View::Settings {
                    self.view = View::Main;
                    self.show_window(ctx, true);
                } else {
                    self.hide_window(ctx);
                }
            }
            UiAction::Copy => {
                self.copied = set_clipboard(&self.transcript);
                if self.copied {
                    self.notice = Some("Copied to clipboard.".to_owned());
                } else {
                    self.notice = Some("The clipboard is busy. Try again.".to_owned());
                }
            }
            UiAction::OpenSettings => {
                self.view = View::Settings;
                self.show_window(ctx, true);
            }
            UiAction::CloseSettings => {
                self.view = View::Main;
                self.show_window(ctx, true);
            }
            UiAction::Calibrate => self.start_calibration(ctx),
            UiAction::CancelCalibration => self.cancel_calibration(ctx),
            UiAction::SetHotkey(choice) => {
                if self.config.hotkey == choice {
                    return;
                }
                let tx = self.events_tx.clone();
                let ctx_clone = ctx.clone();
                match HotkeyService::start(choice, move || {
                    let _ = tx.send(AppEvent::Hotkey);
                    ctx_clone.request_repaint();
                }) {
                    Ok(service) => {
                        self.hotkey = Some(service);
                        self.hotkey_active = true;
                        self.config.hotkey = choice;
                        self.hotkey_error = None;
                        self.notice = Some(format!("Shortcut set to {}.", choice.label()));
                        let _ = self.config.save();
                    }
                    Err(error) => {
                        self.hotkey_error = Some(format!("{} is unavailable: {error}", choice.label()));
                    }
                }
            }
            UiAction::ToggleClear => {
                self.config.clear_after_copy = !self.config.clear_after_copy;
                let _ = self.config.save();
            }
            UiAction::ToggleMinimize => {
                self.config.minimize_after = !self.config.minimize_after;
                let _ = self.config.save();
            }
            UiAction::ToggleAutostart => {
                let enabled = !self.config.launch_at_login;
                match platform::set_autostart(enabled) {
                    Ok(()) => {
                        self.config.launch_at_login = enabled;
                        let _ = self.config.save();
                        self.notice = Some(if enabled {
                            "Murmur will start with Windows.".to_owned()
                        } else {
                            "Murmur won't start with Windows.".to_owned()
                        });
                    }
                    Err(error) => {
                        self.notice = Some(format!("Could not update startup: {error}"));
                    }
                }
            }
            UiAction::OpenClaude => {
                if let Err(error) = platform::launch_claude() {
                    self.notice = Some(format!("Could not open Claude: {error}"));
                } else {
                    self.notice = Some("Asked Windows to open Claude.".to_owned());
                }
            }
            UiAction::OpenLogs => {
                let path = crate::config::log_path();
                if let Some(parent) = path.parent() {
                    let _ = std::fs::create_dir_all(parent);
                }
                let _ = std::fs::OpenOptions::new()
                    .create(true)
                    .append(true)
                    .open(&path);
                let _ = std::process::Command::new("explorer.exe")
                    .arg(path)
                    .spawn();
            }
            UiAction::RestoreClaude => {
                let tx = self.events_tx.clone();
                let ctx = ctx.clone();
                thread::spawn(move || {
                    if let Ok(mut helper) = HelperSession::start() {
                        let _ = helper.restore();
                        helper.quit();
                    }
                    let _ = tx.send(AppEvent::Restored);
                    ctx.request_repaint();
                });
            }
            UiAction::Quit => self.request_quit(ctx),
        }
    }

    fn on_ui(&mut self, ui: &mut egui::Ui) {
        let ctx = ui.ctx().clone();

        if ui.input(|input| input.key_pressed(egui::Key::Escape)) {
            self.action = Some(match self.phase {
                Phase::Calibration => UiAction::CancelCalibration,
                Phase::Listening => UiAction::CancelSession,
                _ if self.view == View::Settings => UiAction::CloseSettings,
                Phase::Ready | Phase::Error | Phase::Setup | Phase::Idle => UiAction::Dismiss,
                _ => UiAction::Dismiss,
            });
        }
        if ui.input(|input| input.key_pressed(egui::Key::Enter))
            && self.view == View::Main
            && self.phase == Phase::Ready
        {
            self.action = Some(UiAction::Copy);
        }

        self.draw(ui);

        if let Some(action) = self.action.take() {
            self.handle_action(action, &ctx);
        }
    }
}

impl eframe::App for MurmurApp {
    fn logic(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.on_logic(ctx);
    }

    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        self.on_ui(ui);
    }
}

impl Drop for MurmurApp {
    fn drop(&mut self) {
        self.f8_hotkey = None;
        if let Some((_, tx)) = self.session_tx.take() {
            let _ = tx.send(SessionCommand::Cancel);
        }
    }
}

fn register_hotkey(
    choice: HotkeyChoice,
    tx: Sender<AppEvent>,
    ctx: egui::Context,
) -> Result<HotkeyService> {
    HotkeyService::start(choice, move || {
        let _ = tx.send(AppEvent::Hotkey);
        ctx.request_repaint();
    })
}

fn set_clipboard(text: &str) -> bool {
    arboard::Clipboard::new()
        .and_then(|mut clipboard| clipboard.set_text(text.to_owned()))
        .is_ok()
}

fn word_count(text: &str) -> usize {
    text.split_whitespace().count()
}

