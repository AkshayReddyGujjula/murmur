$ErrorActionPreference = 'Stop'
$sttRoot = Split-Path -Parent $PSScriptRoot
$sttCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sttTestDir = Join-Path ([IO.Path]::GetTempPath()) ('murmur-transport-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sttTestDir | Out-Null
$sttTestExe = Join-Path $sttTestDir 'TransportTests.exe'
& $sttCompiler /nologo /target:exe /platform:x64 /warnaserror+ "/out:$sttTestExe" (Join-Path $sttRoot 'next\Windows\PseudoTerminal.cs') (Join-Path $sttRoot 'next\Tests\TransportTests.cs')
if ($LASTEXITCODE -ne 0) { throw "Transport test compilation failed: $LASTEXITCODE" }
& $sttTestExe
if ($LASTEXITCODE -ne 0) { throw "Transport tests failed: $LASTEXITCODE" }
