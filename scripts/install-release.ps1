param([switch]$Launch)

$ErrorActionPreference = 'Stop'
if (-not $env:LOCALAPPDATA) { throw 'Murmur requires Windows and a local user profile.' }
$sourceExe = Join-Path $PSScriptRoot 'Murmur.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw 'Extract the complete Murmur download before running Install.ps1.' }
$installDirectory = Join-Path $env:LOCALAPPDATA 'Murmur'
$installedExe = Join-Path $installDirectory 'Murmur.exe'
$running = Get-Process Murmur -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe -or $_.Path -eq $sourceExe }
if ($running) { throw 'Quit Murmur from its tray menu before installing or updating it.' }

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
# Fresh bytes inherit the normal destination permissions, rather than a development
# workspace's Low integrity label. Existing sign-in, settings and data are preserved.
$stagingExe = Join-Path $installDirectory 'Murmur.next.exe'
[IO.File]::WriteAllBytes($stagingExe, [IO.File]::ReadAllBytes($sourceExe))
Move-Item -LiteralPath $stagingExe -Destination $installedExe -Force
$location = Join-Path $installDirectory 'data-location.txt'
$portableData = Join-Path $PSScriptRoot 'data'
if (-not (Test-Path -LiteralPath $location) -and (Test-Path -LiteralPath $portableData -PathType Container)) {
    [IO.File]::WriteAllText($location, $portableData)
}
$programs = [Environment]::GetFolderPath('Programs')
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs 'Murmur.lnk'))
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Save()
Write-Output "Installed Murmur to $installDirectory. Open Murmur from the Start menu."
if ($Launch) { Start-Process -FilePath $installedExe -WorkingDirectory $installDirectory -WindowStyle Hidden }
