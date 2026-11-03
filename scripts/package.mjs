import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const version = readFileSync(join(root, 'VERSION'), 'utf8').trim();
if (!/^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$/.test(version)) throw new Error('Invalid VERSION.');
if (process.platform !== 'win32') throw new Error('Build the Windows package on Windows.');
const build = spawnSync(process.execPath, [join(root, 'scripts', 'build.mjs'), 'build'], { cwd: root, stdio: 'inherit', windowsHide: true });
if (build.error) throw build.error;
if (build.status !== 0) process.exit(build.status || 1);

const releaseDirectory = join(root, 'release');
const staging = join(releaseDirectory, `staging-${version}-${Date.now()}`);
mkdirSync(staging, { recursive: true });
writeFileSync(join(staging, 'Murmur.exe'), readFileSync(join(root, 'build', 'Murmur.exe')));
writeFileSync(join(staging, 'Install.ps1'), readFileSync(join(root, 'scripts', 'install-release.ps1')));
writeFileSync(join(staging, 'README.txt'), `Murmur ${version} | Windows x64 beta

Ctrl+Space. Speak. Ctrl+Space. Your words are copied.

REQUIREMENTS
Windows 10 1809 or later, .NET Framework 4.8, a microphone, internet access,
and a native Claude Code installation with a Claude.ai account that supports voice.
Murmur currently expects Claude at %USERPROFILE%\\.local\\bin\\claude.exe.

INSTALL
1. Extract this entire ZIP to a folder.
2. Quit any running Murmur from its tray menu.
3. Open PowerShell in the extracted folder and run:
   .\\Install.ps1 -Launch
4. Open the tray menu > Account > Sign in to Claude.
   Complete Claude's first-time setup if requested.
5. Click a text field, press Ctrl+Space, wait for the bars, and speak.
   Press Ctrl+Space again to finish. Ctrl+V pastes the result.
   Enable Auto paste in the tray menu to paste automatically.

The installer creates a Start menu shortcut. It needs no administrator access.
Updates preserve existing Murmur data. Fresh installs store data under
%LOCALAPPDATA%\\Murmur\\data. This ZIP contains no account or transcript data.

Murmur and its installer are unsigned beta software. If local policy prevents
running a PowerShell script, the extracted Murmur.exe can run directly from a
normal writable folder; keep that folder in place because its data lives beside it.

PRIVACY AND LIMITS
Claude Code transcribes audio through Anthropic, not locally. Murmur uses its own
Claude profile. It never submits the transcript as an agent prompt or presses Enter.
It writes the result to the Windows clipboard. Recent history is encrypted for
your Windows user. No transcript text is written to Murmur's log.
The animated waveform is a visual meter; it does not change recognition quality.
Long recordings and end-to-end latency are not guaranteed. Claude CLI changes can
affect compatibility. Windows can block automatic paste into elevated applications.

SOURCE, HELP AND UPDATES
https://github.com/AkshayReddyGujjula/murmur
https://github.com/AkshayReddyGujjula/murmur/releases
`);
const files = ['Murmur.exe', 'Install.ps1', 'README.txt'];
const digest = file => createHash('sha256').update(readFileSync(file)).digest('hex');
writeFileSync(join(staging, 'SHA256SUMS.txt'), files.map(name => `${digest(join(staging, name))}  ${name}`).join('\n') + '\n');
files.push('SHA256SUMS.txt');
const archive = join(releaseDirectory, `Murmur-${version}-windows-x64.zip`);
const zipped = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
  "$ErrorActionPreference='Stop'; $files='Murmur.exe','Install.ps1','README.txt','SHA256SUMS.txt' | ForEach-Object { Join-Path $env:MURMUR_PACKAGE_STAGING $_ }; Compress-Archive -LiteralPath $files -DestinationPath $env:MURMUR_PACKAGE_ARCHIVE -CompressionLevel Optimal -Force"], {
  windowsHide: true, stdio: 'inherit', env: { ...process.env, MURMUR_PACKAGE_STAGING: staging, MURMUR_PACKAGE_ARCHIVE: archive },
});
if (zipped.error) throw zipped.error;
if (zipped.status !== 0) process.exit(zipped.status || 1);
writeFileSync(archive + '.sha256', `${digest(archive)}  ${archive.split(/[\\/]/).pop()}\n`);
console.log(`Packaged ${archive}. Only the executable, installer, readme and checksums are included.`);
