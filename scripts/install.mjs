import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
if (args.length > 1 || (args.length === 1 && args[0] !== '--launch'))
  throw new Error('Usage: node scripts/install.mjs [--launch]');
if (!process.env.LOCALAPPDATA) throw new Error('Windows LOCALAPPDATA is required.');
const destination = join(process.env.LOCALAPPDATA, 'Murmur');
const source = join(root, 'build', 'Murmur.exe');
if (!existsSync(source)) throw new Error('Build first: node scripts/build.mjs build');
const running = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
  "$ErrorActionPreference = 'Stop'; if (Get-Process Murmur -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $env:MURMUR_SOURCE_EXE -or $_.Path -eq $env:MURMUR_INSTALLED_EXE }) { exit 3 }"], {
  windowsHide: true, encoding: 'utf8', env: { ...process.env, MURMUR_SOURCE_EXE: source, MURMUR_INSTALLED_EXE: join(destination, 'Murmur.exe') },
});
if (running.error || running.status !== 0) throw new Error('Quit Murmur before installing or updating it.');

// Create fresh files with the destination directory's normal user permissions.
// CopyFile can carry the workspace's Low integrity label to the installed binary.
// Never change the workspace's ACLs or the user's Windows security settings.
mkdirSync(destination, { recursive: true });
const stagedExe = join(destination, 'Murmur.next.exe');
// A previous failed installation may leave a staging file. It contains only our executable.
writeFileSync(stagedExe, readFileSync(source));
try { renameSync(stagedExe, join(destination, 'Murmur.exe')); }
catch (error) { throw new Error('Quit the installed Murmur before updating it. ' + error.message); }

// Keep the existing owned data at its original path. Claude's completed workspace
// setup is tied to that path; relocating it would require user setup again.
// No credentials/history are copied, merged, printed or edited by installation.
const oldData = join(root, 'build', 'data');
const location = join(destination, 'data-location.txt');
if (!existsSync(location) && existsSync(oldData)) writeFileSync(location, oldData, { flag: 'wx' });

const shortcut = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', '-'], {
  windowsHide: true, encoding: 'utf8', env: { ...process.env, MURMUR_INSTALL_DIRECTORY: destination },
  input: "$ErrorActionPreference = 'Stop'\n" +
    "$folder = [Environment]::GetFolderPath('Programs')\n" +
    "$link = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $folder 'Murmur.lnk'))\n" +
    "$link.TargetPath = Join-Path $env:MURMUR_INSTALL_DIRECTORY 'Murmur.exe'\n" +
    "$link.WorkingDirectory = $env:MURMUR_INSTALL_DIRECTORY\n" +
    "$link.Save()\n",
});
if (shortcut.error || shortcut.status !== 0) throw new Error('Installed, but the Start menu shortcut could not be created. ' + (shortcut.error?.message || shortcut.stderr));
console.log(`Installed ${join(destination, 'Murmur.exe')}. Launch Murmur from the Start menu.`);
if (args[0] === '--launch') {
  const app = spawn(join(destination, 'Murmur.exe'), [], { cwd: destination, detached: true, stdio: 'ignore', windowsHide: true });
  app.on('error', error => { console.error('Could not launch Murmur: ' + error.message); process.exitCode = 1; });
  app.unref();
}
