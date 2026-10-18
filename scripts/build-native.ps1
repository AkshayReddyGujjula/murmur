param(
  [switch]$NoZip
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
  throw "Portable Rust toolchain not found. Run the bootstrap/setup first."
}

$env:RUSTUP_HOME = $rustupHome
$env:CARGO_HOME = $cargoHome
$env:PATH = "$cargoHome\bin;$env:PATH"
$env:CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = $linker
$env:RUSTFLAGS = "-C link-self-contained=yes"
$env:TMP = $tmp
$env:TEMP = $tmp
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host "Building the 23 KB Claude bridge..."
& (Join-Path $PSScriptRoot "build-helper.ps1")
if ($LASTEXITCODE -ne 0) {
  throw "Bridge build failed."
}

Write-Host "Building Murmur (release, LTO, stripped)..."
Push-Location (Join-Path $root "app")
try {
  cargo build --release --locked --bin murmur
  if ($LASTEXITCODE -ne 0) {
    throw "Native app build failed."
  }
} finally {
  Pop-Location
}

$exe = Join-Path $root "app\target\release\murmur.exe"
$bridge = Join-Path $root "resources\bin\ClaudeBridge.exe"
if (-not (Test-Path $exe)) { throw "Release executable not found: $exe" }
if (-not (Test-Path $bridge)) { throw "Bridge executable not found: $bridge" }

$releaseRoot = Join-Path $root "release"
$portable = Join-Path $releaseRoot "Murmur"
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Copy-Item $exe (Join-Path $portable "Murmur.exe") -Force
Copy-Item $bridge (Join-Path $portable "ClaudeBridge.exe") -Force

# Rust's GNU target can depend on the MinGW runtime DLLs. Include them when present
# so the portable folder works on a machine with no Rust toolchain.
foreach ($dll in @("libgcc_s_seh-1.dll", "libwinpthread-1.dll")) {
  $source = Join-Path $tc "bin\$dll"
  if (Test-Path $source) {
    Copy-Item $source (Join-Path $portable $dll) -Force
  }
}

$readme = Join-Path $root "README.md"
if (Test-Path $readme) {
  Copy-Item $readme (Join-Path $portable "README.md") -Force
}
$license = Join-Path $root "LICENSE"
if (Test-Path $license) {
  Copy-Item $license (Join-Path $portable "LICENSE") -Force
}
$screenshots = Join-Path $root "docs\screenshots"
if (Test-Path $screenshots) {
  $destScreens = Join-Path $portable "docs\screenshots"
  New-Item -ItemType Directory -Force -Path $destScreens | Out-Null
  Copy-Item (Join-Path $screenshots "*") $destScreens -Force
}
$testing = Join-Path $root "docs\TESTING.md"
if (Test-Path $testing) {
  Copy-Item $testing (Join-Path $portable "TESTING.md") -Force
}
@"
Murmur 0.1.0

1. Keep the Claude desktop app open with its main window running.
2. Run Murmur.exe.
3. Click "Calibrate Claude", hover Claude's mic, press F8.
4. Hover Claude's text box, press F8.
5. Press Ctrl+Space, speak, press Ctrl+Space again.

The prompt is copied automatically. Murmur uses Claude's built-in dictation;
it does not record audio or run a speech model locally.

For the full 5pm test pass, open TESTING.md next to this file.

If Ctrl+Space conflicts with an IME, open Settings and switch to Ctrl+Alt+Space.
"@ | Set-Content -Path (Join-Path $portable "START-HERE.txt") -Encoding UTF8

$exeItem = Get-Item (Join-Path $portable "Murmur.exe")
Write-Host ("Portable build ready: {0} ({1:N0} bytes)" -f $portable, $exeItem.Length)

if (-not $NoZip) {
  $zip = Join-Path $releaseRoot "Murmur-0.1.0-win64.zip"
  Remove-Item $zip -Force -ErrorAction SilentlyContinue
  Compress-Archive -Path (Join-Path $portable "*") -DestinationPath $zip
  $zipItem = Get-Item $zip
  Write-Host ("Archive ready: {0} ({1:N0} bytes)" -f $zipItem.FullName, $zipItem.Length)
}
