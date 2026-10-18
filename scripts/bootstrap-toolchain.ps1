$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$toolchain = Join-Path $root ".toolchain"
$rustupHome = Join-Path $toolchain "rustup"
$cargoHome = Join-Path $toolchain "cargo"
$rustupInit = Join-Path $toolchain "rustup-init.exe"
$tmp = Join-Path $toolchain "tmp"

$cargo = Join-Path $cargoHome "bin\cargo.exe"
if (Test-Path $cargo) {
  Write-Host "Portable Rust toolchain already present at $cargo"
  exit 0
}

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
  throw "Node.js 18+ is required for the one-time toolchain download."
}

New-Item -ItemType Directory -Force -Path $toolchain | Out-Null
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

if (-not (Test-Path $rustupInit)) {
  Write-Host "Downloading rustup-init..."
  & node (Join-Path $PSScriptRoot "download-file.mjs") `
    "https://static.rust-lang.org/rustup/dist/x86_64-pc-windows-msvc/rustup-init.exe" `
    $rustupInit
  if ($LASTEXITCODE -ne 0) {
    throw "Could not download rustup-init."
  }
}

$env:RUSTUP_HOME = $rustupHome
$env:CARGO_HOME = $cargoHome
$env:TMP = $tmp
$env:TEMP = $tmp

Write-Host "Installing the minimal GNU Rust toolchain (no MSVC needed)..."
& $rustupInit -y --no-modify-path --profile minimal `
  --default-host x86_64-pc-windows-gnu `
  --default-toolchain stable
if ($LASTEXITCODE -ne 0) {
  throw "Rust toolchain installation failed."
}

Write-Host "Toolchain ready: $cargo"
Write-Host "Next: .\scripts\build-native.ps1"
