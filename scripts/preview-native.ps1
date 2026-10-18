param(
  [ValidateSet("setup", "idle", "listening", "transcribing", "ready", "error", "settings", "calibration")]
  [string]$Phase = "setup"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$toolchain = Join-Path $root ".toolchain"
$cargoHome = Join-Path $toolchain "cargo"
$rustupHome = Join-Path $toolchain "rustup"
$tc = Join-Path $rustupHome "toolchains\stable-x86_64-pc-windows-gnu"
$linker = Join-Path $tc "lib\rustlib\x86_64-pc-windows-gnu\bin\rust-lld.exe"
$tmp = Join-Path $toolchain "tmp"

if (-not (Test-Path $linker)) {
  throw "Toolchain not found. Run .\scripts\bootstrap-toolchain.ps1 first."
}

$env:RUSTUP_HOME = $rustupHome
$env:CARGO_HOME = $cargoHome
$env:PATH = "$cargoHome\bin;$env:PATH"
$env:CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = $linker
$env:RUSTFLAGS = "-C link-self-contained=yes"
$env:TMP = $tmp
$env:TEMP = $tmp
$env:WGPU_BACKEND = "dx12"
$env:MURMUR_PREVIEW = $Phase
$env:MURMUR_DATA_DIR = Join-Path $root "_research\murmur-data"
$env:MURMUR_LOCAL_DIR = $env:MURMUR_DATA_DIR
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Push-Location (Join-Path $root "app")
try {
  cargo run --bin murmur
} finally {
  Pop-Location
}
