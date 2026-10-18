use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum HotkeyChoice {
    CtrlAltSpace,
    CtrlSpace,
}

impl Default for HotkeyChoice {
    fn default() -> Self {
        Self::CtrlSpace
    }
}

impl HotkeyChoice {
    pub fn label(self) -> &'static str {
        match self {
            Self::CtrlAltSpace => "Ctrl + Alt + Space",
            Self::CtrlSpace => "Ctrl + Space",
        }
    }

    pub fn compact(self) -> &'static str {
        match self {
            Self::CtrlAltSpace => "CTRL ALT SPACE",
            Self::CtrlSpace => "CTRL SPACE",
        }
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq)]
pub struct Point {
    pub x: f64,
    pub y: f64,
}

impl Point {
    pub fn new(x: f64, y: f64) -> Option<Self> {
        if !(0.0..=1.0).contains(&x) || !(0.0..=1.0).contains(&y) {
            return None;
        }
        Some(Self { x, y })
    }

    pub fn display(self) -> String {
        format!("{:.1}% / {:.1}%", self.x * 100.0, self.y * 100.0)
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct Config {
    pub hotkey: HotkeyChoice,
    pub mic: Option<Point>,
    pub focus: Option<Point>,
    pub clear_after_copy: bool,
    pub minimize_after: bool,
    pub launch_at_login: bool,
    pub first_run_done: bool,
    pub last_take_words: u32,
    pub last_take_ms: u64,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            hotkey: HotkeyChoice::CtrlSpace,
            mic: None,
            focus: None,
            clear_after_copy: true,
            minimize_after: true,
            launch_at_login: false,
            first_run_done: false,
            last_take_words: 0,
            last_take_ms: 0,
        }
    }
}

impl Config {
    pub fn is_calibrated(&self) -> bool {
        self.mic.is_some() && self.focus.is_some()
    }

    pub fn load() -> Self {
        let path = config_path();
        match fs::read_to_string(&path) {
            Ok(text) => serde_json::from_str(&text).unwrap_or_default(),
            Err(_) => Self::default(),
        }
    }

    pub fn save(&self) -> Result<()> {
        let path = config_path();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).with_context(|| format!("create {}", parent.display()))?;
        }
        let tmp = path.with_extension("json.tmp");
        let data = serde_json::to_vec_pretty(self)?;
        {
            let mut file = fs::File::create(&tmp)?;
            file.write_all(&data)?;
            file.sync_all()?;
        }
        fs::rename(&tmp, &path).with_context(|| format!("replace {}", path.display()))?;
        Ok(())
    }
}

pub fn app_dir() -> PathBuf {
    if let Some(dir) = std::env::var_os("MURMUR_DATA_DIR") {
        return PathBuf::from(dir);
    }
    let base = std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("Murmur")
}

pub fn local_dir() -> PathBuf {
    if let Some(dir) = std::env::var_os("MURMUR_LOCAL_DIR") {
        return PathBuf::from(dir);
    }
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("Murmur")
}

/// Prefer a portable `data` folder next to the executable. This keeps Murmur
/// usable when it is launched under a restricted token that cannot write to
/// AppData (and keeps a copied portable build self-contained). Explicit
/// `MURMUR_DATA_DIR` / `MURMUR_LOCAL_DIR` overrides still win.
pub fn init_portable_data() {
    if std::env::var_os("MURMUR_DATA_DIR").is_some()
        || std::env::var_os("MURMUR_LOCAL_DIR").is_some()
    {
        return;
    }
    let Ok(exe) = std::env::current_exe() else {
        return;
    };
    let Some(parent) = exe.parent() else {
        return;
    };
    let data = parent.join("data");
    if fs::create_dir_all(&data).is_err() {
        return;
    }
    let probe = data.join(".write-test");
    if fs::write(&probe, b"ok").is_err() {
        return;
    }
    let _ = fs::remove_file(&probe);
    unsafe {
        std::env::set_var("MURMUR_DATA_DIR", &data);
        std::env::set_var("MURMUR_LOCAL_DIR", &data);
    }
}

pub fn config_path() -> PathBuf {
    app_dir().join("config.json")
}

pub fn log_path() -> PathBuf {
    local_dir().join("murmur.log")
}

pub fn log_line(message: &str) {
    eprintln!("{message}");
    let path = log_path();
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    if let Ok(mut file) = fs::OpenOptions::new().create(true).append(true).open(path) {
        let _ = writeln!(file, "{} {message}", timestamp());
    }
}

fn timestamp() -> String {
    // Local time isn't available without another crate. UTC seconds are enough for
    // correlating sessions in a diagnostic log.
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    format!("[{secs}]")
}

pub fn ensure_local_dir() -> Result<()> {
    let dir = local_dir();
    create_dir(&dir)?;
    Ok(())
}

fn create_dir(path: &Path) -> Result<()> {
    fs::create_dir_all(path).with_context(|| format!("create {}", path.display()))?;
    Ok(())
}
