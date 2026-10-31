$ErrorActionPreference = 'Stop'
$sttRoot = Split-Path -Parent $PSScriptRoot
$sttCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sttOutputDir = Join-Path $sttRoot 'next\out'
New-Item -ItemType Directory -Force -Path $sttOutputDir | Out-Null
$sttExe = Join-Path $sttOutputDir 'MurmurProbe.exe'
& $sttCompiler /nologo /target:exe /platform:x64 /warnaserror+ /optimize+ "/out:$sttExe" (Join-Path $sttRoot 'next\Windows\PseudoTerminal.cs') (Join-Path $sttRoot 'next\Probe\PromptExport.cs') (Join-Path $sttRoot 'next\Probe\Program.cs')
if ($LASTEXITCODE -ne 0) { throw "Probe build failed: $LASTEXITCODE" }
Write-Output "Built $sttExe. No provider launched. --login and --live-probe require user approval."
