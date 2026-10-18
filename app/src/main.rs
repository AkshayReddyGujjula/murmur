#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use eframe::egui;

mod app;
mod config;
mod helper;
mod platform;
mod theme;
mod tray;
mod ui;

fn main() -> eframe::Result<()> {
    // D3D12 is the proven backend on this machine. OpenGL contexts render blank
    // in some Windows sessions, so the release build always selects DX12.
    if std::env::var_os("WGPU_BACKEND").is_none() {
        unsafe {
            std::env::set_var("WGPU_BACKEND", "dx12");
        }
    }

    let _ = config::ensure_local_dir();
    let arguments: Vec<String> = std::env::args().collect();
    let start_hidden = arguments.iter().any(|argument| argument == "--hidden");
    let config = config::Config::load();

    let _single_instance = match platform::acquire_single_instance() {
        Ok(Some(instance)) => instance,
        Ok(None) => {
            platform::show_fatal("Murmur is already running. Use the hotkey or the tray icon.");
            return Ok(());
        }
        Err(error) => {
            platform::show_fatal(&format!("Murmur could not start: {error}"));
            return Ok(());
        }
    };

    config::log_line(&format!(
        "starting Murmur {} (hidden={start_hidden}, calibrated={})",
        env!("CARGO_PKG_VERSION"),
        config.is_calibrated()
    ));

    let options = eframe::NativeOptions {
        renderer: eframe::Renderer::Wgpu,
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([760.0, 520.0])
            .with_min_inner_size([760.0, 520.0])
            .with_max_inner_size([760.0, 520.0])
            .with_decorations(false)
            .with_resizable(false)
            .with_taskbar(false)
            .with_always_on_top()
            .with_visible(false)
            .with_title("Murmur"),
        ..Default::default()
    };

    eframe::run_native(
        "Murmur",
        options,
        Box::new(move |cc| {
            Ok(Box::new(app::MurmurApp::new(
                cc,
                config,
                start_hidden,
            )))
        }),
    )
}
