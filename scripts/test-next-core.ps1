$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$core = Join-Path $root 'next\Core\DictationSession.cs'
$tests = Join-Path $root 'next\Tests\CoreTests.cs'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) {
  throw ".NET Framework C# compiler not found at $csc"
}
if (-not (Test-Path $core)) {
  throw "Core source not found at $core"
}
if (-not (Test-Path $tests)) {
  throw "Test source not found at $tests"
}

$sttTestDir = Join-Path ([System.IO.Path]::GetTempPath()) ('next-core-tests-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $sttTestDir | Out-Null
$exe = Join-Path $sttTestDir 'CoreTests.exe'

# Compile ONLY the pure core + test files (no legacy app, native, config).
& $csc /nologo /target:exe /platform:x64 /warnaserror+ /optimize+ /debug- "/out:$exe" /r:System.dll /r:System.Core.dll $core $tests
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
& $exe
exit $LASTEXITCODE
