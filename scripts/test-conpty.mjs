import { spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
if (!process.env.LOCALAPPDATA) throw new Error('Windows LOCALAPPDATA is required.');
const directory = join(process.env.LOCALAPPDATA, 'Murmur', 'diagnostics');
mkdirSync(directory, { recursive: true });
const executable = join(directory, 'conpty-smoke.exe');
const compiler = join(process.env.WINDIR, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const build = spawnSync(compiler, ['/nologo', '/target:exe', '/platform:x64', '/warnaserror+', `/out:${executable}`,
  join(root, 'src', 'Transport', 'OwnedTerminal.cs'), join(root, 'tests', 'ConPtySmokeTests.cs')], { windowsHide: true, encoding: 'utf8' });
if (build.error) throw build.error;
if (build.status !== 0) { console.error(build.stdout + build.stderr); process.exit(build.status || 1); }
// Pipe all three standard streams to reproduce a launcher with redirected I/O.
const test = spawnSync(executable, [], { windowsHide: true, encoding: 'utf8', timeout: 15000 });
if (test.error) throw test.error;
process.stdout.write(test.stdout);
process.stderr.write(test.stderr);
process.exit(test.status || 0);
