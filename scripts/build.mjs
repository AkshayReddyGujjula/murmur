import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const compiler = join(process.env.WINDIR || 'C:\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
if (!existsSync(compiler)) throw new Error('Windows .NET Framework x64 compiler is required. Nothing was installed.');
const command = process.argv[2] || 'build';
if (!['build', 'test', 'test-group'].includes(command)) throw new Error('Usage: node scripts/build.mjs [build|test|test-group <Engine|Core|Audio|Ui|Startup|Wiring>]');

function run(exe, args, environment = process.env) {
  const result = spawnSync(exe, args, { cwd: root, stdio: 'inherit', windowsHide: true, shell: false, env: environment });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status || 1);
}
function sources(folder) {
  if (!existsSync(join(root, folder))) return [];
  return readdirSync(join(root, folder), { withFileTypes: true }).flatMap(entry =>
    entry.isDirectory() ? sources(join(folder, entry.name)) : entry.name.endsWith('.cs') ? [join(folder, entry.name)] : []);
}
const winRefs = ['/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Security.dll'];
function compile(destination, files, extra = []) {
  mkdirSync(dirname(destination), { recursive: true });
  for (const file of files) if (!existsSync(join(root, file))) throw new Error(`Missing source: ${file}`);
  run(compiler, ['/nologo', '/target:exe', '/platform:x64', '/codepage:65001', '/warnaserror+', '/optimize+', `/out:${destination}`, ...extra, ...files.map(file => join(root, file))]);
}
// Backend files the tray app reuses from the proven feasibility harness.
const backend = ['src/Transport/OwnedTerminal.cs', 'src/Handoff/PromptHandoff.cs', 'src/Probe/TerminalScreen.cs', 'src/Probe/IsolatedProfile.cs'];
const appGroups = {
  Engine: () => [...sources('src/App/Engine'), ...backend],
  Core: () => sources('src/App/Core'),
  Audio: () => sources('src/App/Audio'),
  Ui: () => sources('src/App/Ui'),
  Startup: () => [...sources('src/App'), ...backend],
  Wiring: () => [...sources('src/App'), ...backend],
};
const temporary = join(root, '.test-output', 'tmp');
const testEnvironment = { ...process.env, TEMP: temporary, TMP: temporary };
function appGroup(name, required) {
  const test = `tests/App/${name}Tests.cs`;
  const files = appGroups[name]();
  if (!files.length || !existsSync(join(root, test))) {
    if (required) throw new Error(`App group ${name} needs src/App/${name}/*.cs and ${test}.`);
    return;
  }
  mkdirSync(temporary, { recursive: true });
  const destination = join(root, '.test-output', `app-${name.toLowerCase()}.exe`);
  compile(destination, [...files, test], [...winRefs, `/main:Murmur.Tests.App.${name}Tests`]);
  run(destination, [], testEnvironment);
}
if (command === 'test-group') {
  const name = process.argv[3];
  if (!appGroups[name]) throw new Error('Unknown group. Choose Engine, Core, Audio, Ui, Startup or Wiring.');
  appGroup(name, true);
  console.log(`App group ${name} passed.`);
} else if (command === 'test') {
  mkdirSync(temporary, { recursive: true });
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
  compile(inert, [...probeSources(), 'tests/InertTests.cs'], ['/main:Murmur.Tests.InertTests']);
  run(inert, [], testEnvironment);
  for (const name of Object.keys(appGroups)) appGroup(name, false);
  console.log('Offline component checks passed. No real provider or microphone was tested.');
} else {
  const destination = join(root, 'build', 'Murmur.Probe.exe');
  compile(destination, probeSources());
  console.log(`Built ${destination}. The build does not launch it.`);
  if (existsSync(join(root, 'src/App/Program.cs'))) {
    const app = join(root, 'build', 'Murmur.exe');
    const icon = join(root, 'assets', 'murmur.ico');
    compile(app, [...sources('src/App'), ...backend], ['/target:winexe', ...winRefs, ...(existsSync(icon) ? [`/win32icon:${icon}`] : [])]);
    console.log(`Built ${app}. The build does not launch it.`);
  }
}
function probeSources() { return [...sources('src/Probe'), ...sources('src/Transport'), ...sources('src/Handoff')]; }
