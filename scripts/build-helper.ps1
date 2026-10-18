$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'native\ClaudeBridge\ClaudeBridge.cs'
$outDir = Join-Path $root 'resources\bin'
$outFile = Join-Path $outDir 'ClaudeBridge.exe'
$icon = Join-Path $root 'build\icon.ico'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) {
  throw ".NET Framework C# compiler not found at $csc"
}
if (-not (Test-Path $source)) {
  throw "Bridge source not found at $source"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$arguments = @(
  '/nologo',
  '/target:exe',
  '/platform:x64',
  '/optimize+',
  '/debug-',
  "/out:$outFile",
  '/r:System.dll',
  '/r:System.Core.dll',
  '/r:System.Windows.Forms.dll',
  '/r:System.Drawing.dll'
)

if (Test-Path $icon) {
  $arguments += "/win32icon:$icon"
}

$arguments += $source

& $csc $arguments
if ($LASTEXITCODE -ne 0) {
  throw "ClaudeBridge build failed with exit code $LASTEXITCODE"
}
if (-not (Test-Path $outFile)) {
  throw "ClaudeBridge.exe was not produced"
}

$file = Get-Item $outFile
Write-Host ("Built {0} ({1:N0} bytes)" -f $file.FullName, $file.Length)
