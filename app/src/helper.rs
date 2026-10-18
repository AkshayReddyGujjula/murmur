use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::time::{Duration, Instant};

use anyhow::{Context, Result, anyhow, bail};
use base64::Engine;
use base64::engine::general_purpose::STANDARD as B64;

use crate::config::Point;

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[derive(Debug, Clone)]
pub struct RectI {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
}

#[derive(Debug, Clone)]
pub struct WindowInfo {
    pub hwnd: isize,
    pub pid: u32,
    pub title: String,
    pub process: String,
    pub window: RectI,
    pub client: RectI,
    pub minimized: bool,
    pub visible: bool,
}

impl WindowInfo {
    pub fn is_usable(&self) -> bool {
        self.client.w >= 500 && self.client.h >= 380
    }
}

pub struct HelperSession {
    child: Child,
    stdin: ChildStdin,
    rx: Receiver<String>,
    closed: bool,
}

impl HelperSession {
    pub fn start() -> Result<Self> {
        let exe = find_bridge_exe()?;
        crate::config::log_line(&format!("bridge: starting {}", exe.display()));

        let mut child = Command::new(&exe)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .with_context(|| format!("start {}", exe.display()))?;

        let stdin = child.stdin.take().context("bridge stdin unavailable")?;
        let stdout = child.stdout.take().context("bridge stdout unavailable")?;
        let (tx, rx) = mpsc::channel::<String>();

        std::thread::Builder::new()
            .name("murmur-bridge-reader".into())
            .spawn(move || {
                let reader = BufReader::new(stdout);
                for line in reader.lines() {
                    match line {
                        Ok(line) => {
                            if tx.send(line).is_err() {
                                break;
                            }
                        }
                        Err(_) => break,
                    }
                }
            })
            .context("spawn bridge reader")?;

        let mut session = Self {
            child,
            stdin,
            rx,
            closed: false,
        };
        session
            .request("PING", Duration::from_secs(4), |line| {
                line.starts_with("PONG")
            })
            .context("Claude bridge did not answer PING")?;
        Ok(session)
    }

    fn request<F>(
        &mut self,
        command: &str,
        timeout: Duration,
        terminal: F,
    ) -> Result<Vec<String>>
    where
        F: Fn(&str) -> bool,
    {
        if self.closed {
            bail!("bridge session is closed");
        }
        writeln!(self.stdin, "{command}").context("write bridge command")?;
        self.stdin.flush().context("flush bridge command")?;

        let deadline = Instant::now() + timeout;
        let mut lines = Vec::new();
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                bail!("bridge timed out after {timeout:?}: {command}");
            }
            match self.rx.recv_timeout(remaining) {
                Ok(line) => {
                    if let Some(error) = parse_error(&line) {
                        bail!("{error}");
                    }
                    if let Some(encoded) = line.strip_prefix("LOG ") {
                        crate::config::log_line(&format!("bridge: {}", decode_b64(encoded)));
                        continue;
                    }
                    if line.starts_with("TRANSCRIBING") {
                        continue;
                    }
                    let done = terminal(&line);
                    lines.push(line);
                    if done {
                        return Ok(lines);
                    }
                }
                Err(RecvTimeoutError::Timeout) => {
                    bail!("bridge timed out after {timeout:?}: {command}");
                }
                Err(RecvTimeoutError::Disconnected) => {
                    bail!("Claude bridge exited while running: {command}");
                }
            }
        }
    }

    pub fn find(&mut self) -> Result<Option<WindowInfo>> {
        let lines = self.request("FIND", Duration::from_secs(5), |line| {
            line.starts_with("WINDOW")
        })?;
        Ok(lines.iter().find_map(|line| parse_window(line)))
    }

    pub fn focus(&mut self) -> Result<(WindowInfo, isize)> {
        let lines = self.request("FOCUS", Duration::from_secs(8), |line| {
            line.starts_with("FOCUSED")
        })?;
        let window = lines
            .iter()
            .find_map(|line| parse_window(line))
            .ok_or_else(|| anyhow!("bridge did not report the Claude window"))?;
        let focused = lines
            .iter()
            .find(|line| line.starts_with("FOCUSED"))
            .and_then(|line| parse_fields(line).get("previous").copied())
            .and_then(|value| value.parse::<isize>().ok())
            .unwrap_or(0);
        Ok((window, focused))
    }

    pub fn bounds(&mut self) -> Result<WindowInfo> {
        let lines = self.request("BOUNDS", Duration::from_secs(5), |line| {
            line.starts_with("WINDOW")
        })?;
        lines
            .iter()
            .find_map(|line| parse_window(line))
            .ok_or_else(|| anyhow!("bridge did not report window bounds"))
    }

    pub fn cursor(&mut self) -> Result<(i32, i32)> {
        let lines = self.request("CURSOR", Duration::from_secs(4), |line| {
            line.starts_with("CURSOR")
        })?;
        let line = lines
            .iter()
            .find(|line| line.starts_with("CURSOR"))
            .ok_or_else(|| anyhow!("bridge did not report the cursor"))?;
        let fields = parse_fields(line);
        let x = fields
            .get("x")
            .ok_or_else(|| anyhow!("cursor x missing"))?
            .parse::<i32>()?;
        let y = fields
            .get("y")
            .ok_or_else(|| anyhow!("cursor y missing"))?
            .parse::<i32>()?;
        Ok((x, y))
    }

    pub fn begin(
        &mut self,
        mic: Point,
        focus: Point,
        hide_mode: &str,
        fresh: bool,
        minimize_after: bool,
    ) -> Result<String> {
        let command = format!(
            "BEGIN {:.6} {:.6} {:.6} {:.6} {} {} {}",
            mic.x,
            mic.y,
            focus.x,
            focus.y,
            hide_mode,
            if fresh { 1 } else { 0 },
            if minimize_after { 1 } else { 0 }
        );
        let lines = self.request(&command, Duration::from_secs(12), |line| {
            line.starts_with("RECORDING")
        })?;
        let line = lines
            .iter()
            .find(|line| line.starts_with("RECORDING"))
            .ok_or_else(|| anyhow!("bridge did not report recording"))?;
        Ok(parse_fields(line)
            .get("mode")
            .copied()
            .unwrap_or("alpha")
            .to_string())
    }

    pub fn click_through(&mut self, enabled: bool) -> Result<()> {
        let command = format!("CLICKTHROUGH {}", if enabled { 1 } else { 0 });
        self.request(&command, Duration::from_secs(4), |line| {
            line.starts_with("CLICKTHROUGH")
        })?;
        Ok(())
    }

    pub fn end(&mut self, mic: Point, clear_after: bool) -> Result<Option<String>> {
        let command = format!(
            "END {:.6} {:.6} {}",
            mic.x,
            mic.y,
            if clear_after { 1 } else { 0 }
        );
        let lines = self.request(&command, Duration::from_secs(20), |line| {
            line.starts_with("TEXT") || line.starts_with("EMPTY")
        })?;
        Ok(transcript_from_lines(&lines))
    }

    pub fn cancel(&mut self) -> Result<()> {
        self.request("CANCEL", Duration::from_secs(8), |line| {
            line.starts_with("CANCELLED")
        })?;
        Ok(())
    }

    pub fn activate(&mut self, hwnd: isize) -> Result<()> {
        let command = format!("ACTIVATE {hwnd}");
        self.request(&command, Duration::from_secs(5), |line| {
            line.starts_with("ACTIVATED")
        })?;
        Ok(())
    }

    pub fn restore(&mut self) -> Result<()> {
        self.request("RESTORE", Duration::from_secs(5), |line| {
            line.starts_with("RESTORED")
        })?;
        Ok(())
    }

    pub fn quit(mut self) {
        self.shutdown();
    }

    fn shutdown(&mut self) {
        if self.closed {
            return;
        }
        self.closed = true;
        let _ = writeln!(self.stdin, "QUIT");
        let _ = self.stdin.flush();
        let deadline = Instant::now() + Duration::from_millis(900);
        while Instant::now() < deadline {
            if let Ok(Some(_)) = self.child.try_wait() {
                return;
            }
            std::thread::sleep(Duration::from_millis(35));
        }
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

impl Drop for HelperSession {
    fn drop(&mut self) {
        self.shutdown();
    }
}

pub fn find_bridge_exe() -> Result<PathBuf> {
    let mut candidates = Vec::new();
    if let Some(path) = std::env::var_os("MURMUR_BRIDGE") {
        candidates.push(PathBuf::from(path));
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent() {
            candidates.push(parent.join("ClaudeBridge.exe"));
        }
        for ancestor in exe.ancestors() {
            candidates.push(ancestor.join("resources").join("bin").join("ClaudeBridge.exe"));
        }
    }
    if let Ok(cwd) = std::env::current_dir() {
        candidates.push(cwd.join("resources").join("bin").join("ClaudeBridge.exe"));
        candidates.push(
            cwd.join("..")
                .join("resources")
                .join("bin")
                .join("ClaudeBridge.exe"),
        );
    }
    for candidate in &candidates {
        if candidate.is_file() {
            return Ok(candidate.clone());
        }
    }
    let list = candidates
        .iter()
        .map(|path| path.display().to_string())
        .collect::<Vec<_>>()
        .join("\n");
    bail!("ClaudeBridge.exe was not found. Looked in:\n{list}");
}

fn parse_window(line: &str) -> Option<WindowInfo> {
    if !line.starts_with("WINDOW ") {
        return None;
    }
    let fields = parse_fields(line);
    let hwnd = fields.get("hwnd")?.parse::<isize>().ok()?;
    let pid = fields.get("pid")?.parse::<u32>().ok()?;
    let title = decode_b64(fields.get("title").copied().unwrap_or(""));
    let process = decode_b64(fields.get("process").copied().unwrap_or(""));
    let window = parse_rect(fields.get("win")?)?;
    let client = parse_rect(fields.get("client")?)?;
    let minimized = fields.get("minimized").map(|v| *v == "1").unwrap_or(false);
    let visible = fields.get("visible").map(|v| *v == "1").unwrap_or(true);
    Some(WindowInfo {
        hwnd,
        pid,
        title,
        process,
        window,
        client,
        minimized,
        visible,
    })
}

fn parse_rect(value: &str) -> Option<RectI> {
    let mut parts = value.split(',');
    let x = parts.next()?.parse::<i32>().ok()?;
    let y = parts.next()?.parse::<i32>().ok()?;
    let w = parts.next()?.parse::<i32>().ok()?;
    let h = parts.next()?.parse::<i32>().ok()?;
    Some(RectI { x, y, w, h })
}

fn transcript_from_lines(lines: &[String]) -> Option<String> {
    for line in lines {
        if line.starts_with("TEXT ") {
            // parse_fields skips its first token, so give it the whole line
            // ("TEXT"), not the substring after it. Passing the substring would
            // skip the `text=` field itself and every transcript would look empty.
            let fields = parse_fields(line);
            if let Some(encoded) = fields.get("text") {
                return Some(decode_b64(encoded));
            }
        }
        if line.starts_with("EMPTY") {
            return None;
        }
    }
    None
}

fn parse_fields(line: &str) -> HashMap<&str, &str> {
    let mut fields = HashMap::new();
    for token in line.split_whitespace().skip(1) {
        if let Some((key, value)) = token.split_once('=') {
            fields.insert(key, value);
        }
    }
    fields
}

fn parse_error(line: &str) -> Option<String> {
    if !line.starts_with("ERROR ") {
        return None;
    }
    let fields = parse_fields(line);
    let message = fields
        .get("message")
        .map(|value| decode_b64(value))
        .unwrap_or_else(|| "Unknown bridge error".to_string());
    let code = fields
        .get("code")
        .map(|value| decode_b64(value))
        .unwrap_or_default();
    if code.is_empty() {
        Some(message)
    } else {
        Some(format!("{message} ({code})"))
    }
}

fn decode_b64(value: &str) -> String {
    B64.decode(value)
        .ok()
        .and_then(|bytes| String::from_utf8(bytes).ok())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_text_transcript_with_base64_padding() {
        let text = "Mock transcript, with punctuation and OAuth.";
        let line = format!("TEXT text={} chars={}", B64.encode(text), text.len());
        assert_eq!(transcript_from_lines(&[line]), Some(text.to_owned()));
    }

    #[test]
    fn ignores_transcribing_line_before_text() {
        let text = "Second take.";
        let lines = vec![
            "TRANSCRIBING".to_owned(),
            format!("TEXT text={} chars={}", B64.encode(text), text.len()),
        ];
        assert_eq!(transcript_from_lines(&lines), Some(text.to_owned()));
    }

    #[test]
    fn empty_transcript_is_none() {
        assert_eq!(transcript_from_lines(&["EMPTY".to_owned()]), None);
    }
}
