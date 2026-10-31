# Compiles ONLY the two pure helper/test files with the Windows .NET
# Framework 64-bit C# compiler to a unique temporary output, runs the tests,
# and propagates failures. No policy or system setting edits, no installs.
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$helper = Join-Path $repoRoot 'next\Probe\PromptExport.cs'
$tests = Join-Path $repoRoot 'next\Tests\PromptExportTests.cs'

foreach ($file in @($helper, $tests)) {
  if (!(Test-Path -LiteralPath $file)) {
    Write-Error "Missing required file: $file"
    exit 1
  }
}

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $csc)) {
  Write-Error "C# compiler not found: $csc"
  exit 1
}

$outDir = Join-Path ([IO.Path]::GetTempPath()) ('murmur-next-export-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outDir | Out-Null
$exe = Join-Path $outDir 'PromptExportTests.exe'

& $csc /nologo /t:exe "/out:$exe" $helper $tests
if ($LASTEXITCODE -ne 0) {
  Write-Error "Compile failed with exit code $LASTEXITCODE"
  exit $LASTEXITCODE
}

& $exe
$testExit = $LASTEXITCODE
if ($testExit -ne 0) {
  Write-Error "Tests failed with exit code $testExit"
  exit $testExit
}
