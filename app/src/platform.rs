use std::sync::mpsc;
use std::thread::{self, JoinHandle};
use std::time::Duration;

use anyhow::{Result, bail};
use windows::Win32::Foundation::{
    CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE, HWND, LPARAM, RECT, WPARAM,
};
use windows::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_WINDOW_CORNER_PREFERENCE};
use windows::Win32::System::Threading::{CreateMutexW, GetCurrentThreadId};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    HOT_KEY_MODIFIERS, MOD_ALT, MOD_CONTROL, MOD_NOREPEAT, RegisterHotKey, UnregisterHotKey,
};
use windows::Win32::UI::WindowsAndMessaging::{
    FindWindowW, GetMessageW, GetSystemMetrics, GetWindowLongPtrW, GetWindowRect, MessageBoxW,
    PostThreadMessageW, SW_HIDE, SW_SHOW, SW_SHOWNOACTIVATE, SWP_NOACTIVATE, SWP_NOMOVE,
    SWP_NOSIZE, SWP_NOZORDER, SWP_SHOWWINDOW, SetForegroundWindow, SetWindowLongPtrW,
    SetWindowPos, ShowWindow, WM_HOTKEY, WM_QUIT, GWL_EXSTYLE, HWND_TOPMOST, MB_ICONERROR, MB_OK,
    SM_CXSCREEN, SM_CYSCREEN, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
};
use windows::core::{PCWSTR, w};

use crate::config::HotkeyChoice;

pub struct SingleInstance {
    handle: HANDLE,
}

impl Drop for SingleInstance {
    fn drop(&mut self) {
        if !self.handle.is_invalid() {
            let _ = unsafe { CloseHandle(self.handle) };
        }
    }
}

pub fn acquire_single_instance() -> Result<Option<SingleInstance>> {
    let handle = unsafe {
        CreateMutexW(
            None,
            false,
            w!("Local\\Murmur.SingleInstance"),
        )
    }?;
    let last_error = unsafe { GetLastError() };
    if last_error == ERROR_ALREADY_EXISTS {
        let _ = unsafe { CloseHandle(handle) };
        return Ok(None);
    }
    Ok(Some(SingleInstance { handle }))
}

pub fn show_fatal(message: &str) {
    let wide_text = to_wide(message);
    let wide_caption = to_wide("Murmur");
    unsafe {
        MessageBoxW(
            None,
            PCWSTR(wide_text.as_ptr()),
            PCWSTR(wide_caption.as_ptr()),
            MB_OK | MB_ICONERROR,
        );
    }
}

fn to_wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

pub fn find_own_window() -> Option<HWND> {
    unsafe { FindWindowW(PCWSTR::null(), w!("Murmur")).ok() }
}

pub fn prepare_overlay(hwnd: HWND) {
    unsafe {
        let mut value: u32 = 2; // DWMWCP_ROUND
        let _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            &mut value as *mut _ as *const _,
            std::mem::size_of::<u32>() as u32,
        );
        set_no_activate(hwnd, true);
    }
}

pub fn set_no_activate(hwnd: HWND, enabled: bool) {
    unsafe {
        let current = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
        let mut style = current | WS_EX_TOOLWINDOW.0;
        if enabled {
            style |= WS_EX_NOACTIVATE.0;
        } else {
            style &= !WS_EX_NOACTIVATE.0;
        }
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style as isize);
    }
}

pub fn show_overlay(hwnd: HWND, focus: bool) {
    unsafe {
        set_no_activate(hwnd, !focus);
        if focus {
            let _ = ShowWindow(hwnd, SW_SHOW);
            let _ = SetForegroundWindow(hwnd);
        } else {
            let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }
        center_window(hwnd);
        let _ = SetWindowPos(
            hwnd,
            Some(HWND_TOPMOST),
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW,
        );
    }
}

pub fn hide_overlay(hwnd: HWND) {
    unsafe {
        let _ = ShowWindow(hwnd, SW_HIDE);
    }
}

pub fn focus_overlay(hwnd: HWND) {
    unsafe {
        set_no_activate(hwnd, false);
        let _ = ShowWindow(hwnd, SW_SHOW);
        let _ = SetForegroundWindow(hwnd);
    }
}

pub fn center_window(hwnd: HWND) {
    unsafe {
        let mut rect = RECT::default();
        if GetWindowRect(hwnd, &mut rect).is_err() {
            return;
        }
        let width = rect.right - rect.left;
        let height = rect.bottom - rect.top;
        let screen_width = GetSystemMetrics(SM_CXSCREEN);
        let screen_height = GetSystemMetrics(SM_CYSCREEN);
        let x = (screen_width - width).max(0) / 2;
        let y = (screen_height - height).max(0) / 2;
        let _ = SetWindowPos(
            hwnd,
            None,
            x,
            y,
            0,
            0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE,
        );
    }
}

pub fn resize_window(hwnd: HWND, width: i32, height: i32, y: i32) {
    unsafe {
        let screen_width = GetSystemMetrics(SM_CXSCREEN);
        let x = (screen_width - width).max(0) / 2;
        let _ = SetWindowPos(hwnd, None, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}

pub fn set_vertical_position(hwnd: HWND, y: i32) {
    unsafe {
        let mut rect = RECT::default();
        if GetWindowRect(hwnd, &mut rect).is_err() {
            return;
        }
        let _ = SetWindowPos(
            hwnd,
            None,
            rect.left,
            y,
            0,
            0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE,
        );
    }
}

pub fn cursor_position() -> (i32, i32) {
    use windows::Win32::Foundation::POINT;
    use windows::Win32::UI::WindowsAndMessaging::GetCursorPos;
    let mut point = POINT::default();
    unsafe {
        let _ = GetCursorPos(&mut point);
    }
    (point.x, point.y)
}

pub struct HotkeyService {
    thread_id: u32,
    join: Option<JoinHandle<()>>,
}

impl Drop for HotkeyService {
    fn drop(&mut self) {
        unsafe {
            let _ = PostThreadMessageW(self.thread_id, WM_QUIT, WPARAM(0), LPARAM(0));
        }
        if let Some(join) = self.join.take() {
            let _ = join.join();
        }
    }
}

impl HotkeyService {
    pub fn start<F>(choice: HotkeyChoice, on_press: F) -> Result<Self>
    where
        F: Fn() + Send + 'static,
    {
        let modifiers = match choice {
            HotkeyChoice::CtrlAltSpace => HOT_KEY_MODIFIERS(MOD_CONTROL.0 | MOD_ALT.0 | MOD_NOREPEAT.0),
            HotkeyChoice::CtrlSpace => HOT_KEY_MODIFIERS(MOD_CONTROL.0 | MOD_NOREPEAT.0),
        };
        Self::start_raw(modifiers, 0x20, on_press)
    }

    pub fn start_f8<F>(on_press: F) -> Result<Self>
    where
        F: Fn() + Send + 'static,
    {
        Self::start_raw(HOT_KEY_MODIFIERS(MOD_NOREPEAT.0), 0x77, on_press)
    }

    fn start_raw<F>(modifiers: HOT_KEY_MODIFIERS, key: u32, on_press: F) -> Result<Self>
    where
        F: Fn() + Send + 'static,
    {
        let (ready_tx, ready_rx) = mpsc::channel::<Result<u32, String>>();
        let join = thread::Builder::new()
            .name("murmur-hotkey".into())
            .spawn(move || {
                let thread_id = unsafe { GetCurrentThreadId() };
                let registered = unsafe { RegisterHotKey(None, 1, modifiers, key) };
                if let Err(error) = registered {
                    let _ = ready_tx.send(Err(error.to_string()));
                    return;
                }
                let _ = ready_tx.send(Ok(thread_id));

                let mut message = windows::Win32::UI::WindowsAndMessaging::MSG::default();
                loop {
                    let result = unsafe { GetMessageW(&mut message, None, 0, 0) };
                    if result.0 == 0 || result.0 == -1 {
                        break;
                    }
                    if message.message == WM_HOTKEY {
                        on_press();
                    }
                }
                unsafe {
                    let _ = UnregisterHotKey(None, 1);
                }
            })?;

        match ready_rx.recv_timeout(Duration::from_secs(2)) {
            Ok(Ok(thread_id)) => Ok(Self {
                thread_id,
                join: Some(join),
            }),
            Ok(Err(error)) => {
                let _ = join.join();
                bail!(error)
            }
            Err(_) => {
                let _ = join.join();
                bail!("hotkey registration timed out")
            }
        }
    }
}

pub fn set_autostart(enabled: bool) -> Result<()> {
    use winreg::RegKey;
    use winreg::enums::{HKEY_CURRENT_USER, KEY_QUERY_VALUE, KEY_SET_VALUE};

    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let key = hkcu.open_subkey_with_flags(
        "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
        KEY_QUERY_VALUE | KEY_SET_VALUE,
    )?;
    if enabled {
        let exe = std::env::current_exe()?;
        let command = format!("\"{}\" --hidden", exe.display());
        key.set_value("Murmur", &command)?;
    } else {
        let _ = key.delete_value("Murmur");
    }
    Ok(())
}

pub fn autostart_enabled() -> bool {
    use winreg::RegKey;
    use winreg::enums::HKEY_CURRENT_USER;
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let Ok(key) = hkcu.open_subkey("Software\\Microsoft\\Windows\\CurrentVersion\\Run") else {
        return false;
    };
    key.get_value::<String, _>("Murmur").is_ok()
}

pub fn launch_claude() -> Result<()> {
    // The packaged Windows app registers this AUMID for the Start menu. If the app
    // is already running, the shell asks the existing instance to come forward.
    std::process::Command::new("explorer.exe")
        .arg("shell:AppsFolder\\Claude_pzs8sxrjxfjjc!Claude")
        .spawn()?;
    Ok(())
}
