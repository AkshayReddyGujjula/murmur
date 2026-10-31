import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const compiler = join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
if (!existsSync(compiler)) throw new Error('Windows .NET Framework x64 compiler is required. Nothing was installed.');
const command = process.argv[2] || 'build';
if (!['build', 'test'].includes(command)) throw new Error('Usage: node scripts/build.mjs [build|test]');

function run(exe, args, environment = process.env) {
  const result = spawnSync(exe, args, { cwd: root, stdio: 'inherit', windowsHide: true, shell: false, env: environment });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status || 1);
}
function sources(folder) {
  return readdirSync(join(root, folder), { withFileTypes: true }).flatMap(entry =>
    entry.isDirectory() ? sources(join(folder, entry.name)) : entry.name.endsWith('.cs') ? [join(folder, entry.name)] : []);
}
function compile(destination, files, extra = []) {
  mkdirSync(dirname(destination), { recursive: true });
  for (const file of files) if (!existsSync(join(root, file))) throw new Error(`Missing source: ${file}`);
  run(compiler, ['/nologo', '/target:exe', '/platform:x64', '/codepage:65001', '/warnaserror+', '/optimize+', `/out:${destination}`, ...extra, ...files.map(file => join(root, file))]);
}
if (command === 'test') {
  const temporary = join(root, '.test-output', 'tmp');
  mkdirSync(temporary, { recursive: true });
  const testEnvironment = { ...process.env, TEMP: temporary, TMP: temporary };
  const groups = [
    ['control', ['src/Probe/ProbeControl.cs', 'tests/ControlTests.cs']],
    ['options', ['src/Probe/ProbeOptions.cs', 'tests/OptionsTests.cs']],
    ['inbox', ['src/Probe/ProbeInbox.cs', 'src/Probe/ProbeControl.cs', 'tests/InboxTests.cs']],
    ['transport', ['src/Transport/OwnedTerminal.cs', 'tests/TransportTests.cs']],
    ['handoff', ['src/Handoff/PromptHandoff.cs', 'tests/HandoffTests.cs']],
    ['screen', ['src/Probe/TerminalScreen.cs', 'tests/TerminalScreenTests.cs']],
  ];
  for (const [name, files] of groups) {
    const destination = join(root, '.test-output', `${name}.exe`);
    compile(destination, files);
    run(destination, [], testEnvironment);
  }
  const inert = join(root, '.test-output', 'inert.exe');
  compile(inert, [...sources('src'), 'tests/InertTests.cs'], ['/main:Murmur.Tests.InertTests']);
  run(inert, [], testEnvironment);
  console.log('Offline component checks passed. No real provider or microphone was tested.');
} else {
  const destination = join(root, 'build', 'Murmur.Probe.exe');
  compile(destination, sources('src'));
  console.log(`Built ${destination}. The build does not launch it.`);
}
