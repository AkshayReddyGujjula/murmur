use std::sync::mpsc::Sender;

use anyhow::{Context, Result};
use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
use tray_icon::{MouseButton, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent};

#[derive(Debug, Clone, Copy)]
pub enum TrayAction {
    Toggle,
    Start,
    Settings,
    Quit,
}

pub struct TrayState {
    _tray: TrayIcon,
}

pub fn create(
    ctx: egui::Context,
    actions: Sender<TrayAction>,
    hotkey_label: &str,
) -> Result<TrayState> {
    let menu = Menu::new();
    let open = MenuItem::with_id("open", format!("Open Murmur  ·  {hotkey_label}"), true, None);
    let start = MenuItem::with_id("start", "Start a take", true, None);
    let settings = MenuItem::with_id("settings", "Settings", true, None);
    let separator = PredefinedMenuItem::separator();
    let quit = MenuItem::with_id("quit", "Quit Murmur", true, None);
    menu.append_items(&[&open, &start, &settings, &separator, &quit])
        .context("build tray menu")?;

    let tray = TrayIconBuilder::new()
        .with_icon(make_icon()?)
        .with_tooltip("Murmur — Claude listens. You paste.")
        .with_menu(Box::new(menu))
        .with_menu_on_left_click(false)
        .build()
        .context("create tray icon")?;

    let click_tx = actions.clone();
    let click_ctx = ctx.clone();
    TrayIconEvent::set_event_handler(Some(move |event: TrayIconEvent| {
        if let TrayIconEvent::Click {
            button: MouseButton::Left,
            button_state: MouseButtonState::Up,
            ..
        } = event
        {
            let _ = click_tx.send(TrayAction::Toggle);
            click_ctx.request_repaint();
        }
    }));

    let menu_tx = actions;
    let menu_ctx = ctx;
    MenuEvent::set_event_handler(Some(move |event: MenuEvent| {
        let action = match event.id().as_ref() {
            "open" => Some(TrayAction::Toggle),
            "start" => Some(TrayAction::Start),
            "settings" => Some(TrayAction::Settings),
            "quit" => Some(TrayAction::Quit),
            _ => None,
        };
        if let Some(action) = action {
            let _ = menu_tx.send(action);
            menu_ctx.request_repaint();
        }
    }));

    Ok(TrayState { _tray: tray })
}

fn make_icon() -> Result<tray_icon::Icon> {
    const SIZE: u32 = 32;
    let mut rgba = vec![0u8; (SIZE * SIZE * 4) as usize];

    let mut put = |x: i32, y: i32, color: [u8; 4]| {
        if x < 0 || y < 0 || x >= SIZE as i32 || y >= SIZE as i32 {
            return;
        }
        let index = ((y as u32 * SIZE + x as u32) * 4) as usize;
        rgba[index..index + 4].copy_from_slice(&color);
    };

    // Rounded dark plate with a blue rim.
    for y in 0..SIZE as i32 {
        for x in 0..SIZE as i32 {
            let dx = (x as f32 - 15.5).abs();
            let dy = (y as f32 - 15.5).abs();
            let inside = dx <= 14.0 && dy <= 14.0 && (dx < 10.0 || dy < 10.0);
            if inside {
                let border = dx > 12.4 || dy > 12.4;
                let color = if border {
                    [70, 120, 210, 255]
                } else {
                    [8, 18, 32, 255]
                };
                put(x, y, color);
            }
        }
    }

    // Five waveform bars.
    let bars = [(11, 10, 12), (14, 7, 18), (17, 5, 22), (20, 8, 16), (23, 11, 10)];
    for (x, top, bottom) in bars {
        for y in top..=bottom {
            put(x, y, [245, 249, 255, 255]);
        }
    }

    tray_icon::Icon::from_rgba(rgba, SIZE, SIZE).context("make tray icon")
}
