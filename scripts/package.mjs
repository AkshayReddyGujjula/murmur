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

The easiest download is Murmur-Setup.exe from the releases page below. It is the
same app as Murmur.exe in this ZIP.

WHAT YOU NEED
Windows 10 or 11 (64-bit), a microphone, internet access, and a Claude.ai account.
Voice dictation needs a Claude.ai account sign-in; an API key alone does not work.
Murmur uses Claude Code. If it is missing, Murmur offers to install it for you
(expected at %USERPROFILE%\\.local\\bin\\claude.exe).

INSTALL
1. Extract this entire ZIP to a folder.
2. Double-click Murmur.exe. If Windows says "Windows protected your PC",
   click More info, then Run anyway. Murmur is unsigned beta software.
   Murmur copies itself to %LOCALAPPDATA%\\Murmur, adds a Start menu shortcut
   and starts. No administrator access is needed.
3. If asked, choose Install Claude Code (or Not now, then later use
   Install Claude Code... in the tray menu). Then sign in to your Claude.ai
   account in the browser that opens. If the browser shows a code, copy it,
   click the small Murmur sign-in window, right-click to paste, press Enter.
   The window closes and Murmur says "You're all set!". This is Murmur's own
   sign-in.
4. Click a text field, press Ctrl+Space, wait for the bars, and speak.
   Press Ctrl+Space again to finish. Ctrl+V pastes the result.
   Turn on Auto paste in the tray menu to paste automatically. Esc cancels.

Alternative: open PowerShell in the extracted folder and run
   .\\Install.ps1 -Launch

To update, quit Murmur from its tray menu and run the newer download.
Updates preserve existing Murmur data and sign-in. Fresh installs store data
under %LOCALAPPDATA%\\Murmur\\data. This ZIP contains no account or transcript data.

If Murmur says Claude needs one-time setup, open the tray menu > Account >
Run Claude first-time setup. Answer its questions; for the folder question
choose "Yes, I trust this folder" (press Down, then Enter). When the window
shows an empty box to type in, just close it.

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
const setup = join(releaseDirectory, 'Murmur-Setup.exe');
writeFileSync(setup, readFileSync(join(root, 'build', 'Murmur.exe')));
writeFileSync(setup + '.sha256', `${digest(setup)}  Murmur-Setup.exe\n`);
console.log(`Packaged ${archive} and ${setup}. Only the executable, installer, readme and checksums are included.`);
