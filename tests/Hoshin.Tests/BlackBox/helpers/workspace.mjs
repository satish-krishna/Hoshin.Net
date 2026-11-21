// Builds a real workspace in a scratch folder for the black-box tests and runs `dotnet hoshin.dll` in it.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { delimiter, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tempDir } from './temp.mjs';

const helpersDir = dirname(fileURLToPath(import.meta.url));
export const testsRoot = resolve(helpersDir, '..', '..');
export const repoRoot = resolve(testsRoot, '..', '..');

function findHoshinDll() {
  if (process.env.TEST_HOSHIN_DLL && existsSync(process.env.TEST_HOSHIN_DLL)) return process.env.TEST_HOSHIN_DLL;
  const candidates = [
    join(repoRoot, 'src', 'Hoshin.Cli', 'bin', 'Debug', 'net10.0', 'hoshin.dll'),
    join(repoRoot, 'src', 'Hoshin.Cli', 'bin', 'Release', 'net10.0', 'hoshin.dll'),
    join(testsRoot, 'bin', 'Debug', 'net10.0', 'hoshin.dll'),
  ];
  for (const c of candidates) {
    if (existsSync(c)) return c;
  }
  return candidates[0];
}

export const program = findHoshinDll();
export const node = process.execPath;

function findExe(name) {
  const cmd = process.platform === 'win32' ? 'where' : 'which';
  const r = spawnSync(cmd, [name], { encoding: 'utf8' });
  if (r.status === 0 && r.stdout.trim()) return r.stdout.trim().split(/\r?\n/)[0].trim();
  return name;
}

export const dotnet = findExe('dotnet');
const realGit = findExe('git');

// Minimal environment isolated from the caller's git/gh tooling, keeping dotnet and runtime basics available.
export const bareEnv = {
  PATH: [dirname(dotnet), dirname(process.execPath)].join(delimiter),
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  ...(process.platform === 'win32'
    ? {
        SystemRoot: process.env.SystemRoot ?? 'C:\\Windows',
        COMSPEC: process.env.COMSPEC ?? 'C:\\Windows\\System32\\cmd.exe',
        USERPROFILE: process.env.USERPROFILE ?? '',
        TEMP: process.env.TEMP ?? '',
        TMP: process.env.TMP ?? '',
      }
    : {}),
};

// Every git the tests and the program start sees the same identity and no user configuration.
export const env = {
  ...process.env,
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  GIT_AUTHOR_NAME: 't',
  GIT_AUTHOR_EMAIL: 't@example.com',
  GIT_COMMITTER_NAME: 't',
  GIT_COMMITTER_EMAIL: 't@example.com',
  GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null',
  GIT_CONFIG_NOSYSTEM: '1',
};

export function git(cwd, ...args) {
  const r = spawnSync('git', args, { cwd, encoding: 'utf8', env });
  assert.equal(r.status, 0, `git ${args.join(' ')} in ${cwd}: ${r.stderr}`);
  return r.stdout.trim();
}

export function write(path, text) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, text);
}

export const CHANGE = '2026-11-01-greeting';

/** A repository on `main` with one commit and a bare remote it has pushed to. */
export function repo(path, files = { 'README.md': '# repo\n' }) {
  mkdirSync(path, { recursive: true });
  git(path, 'init', '-q', '-b', 'main');
  for (const [name, text] of Object.entries(files)) write(join(path, name), text);
  git(path, 'add', '.');
  git(path, 'commit', '-q', '-m', 'chore: initial');
  const remote = `${path}.remote.git`;
  git(dirname(path), 'init', '-q', '--bare', '-b', 'main', remote);
  git(path, 'remote', 'add', 'origin', remote);
  git(path, 'push', '-q', '-u', 'origin', 'main');
  git(path, 'remote', 'set-head', 'origin', 'main');
  return path;
}

export const INTENT =
  '```jsonc\n' +
  JSON.stringify({ title: 'Greeting', system: 'demo', author: 't', date: '2026-11-01', status: 'accepted', scope: 'demo' }, null, 2) +
  '\n```\n\n# Intent: Greeting\n';

const example = (name) => readFileSync(join(testsRoot, 'Examples', 'change', '2', 'valid', name), 'utf8');

/** A build log with an entry for each of the given tasks. */
export const buildLog = (tasks) =>
  '```jsonc\n{ "layout": 2 }\n```\n\n# Build log: app\n' +
  tasks.map((t) => `\n### ${t}\n\n\`\`\`jsonc\n{\n  "task": ${JSON.stringify(t)},\n  "commits": ["abc1234"]\n}\n\`\`\`\n`).join('');

/**
 * A workspace: `master` (marker with system demo over `app`, and a change folder with an accepted intent) and `app`.
 */
export function workspace({ marker = {}, masterFiles = {}, repos = ['app'], built = false } = {}) {
  const ws = realpathSync(tempDir('hoshin-ws-'));
  const fullMarker = { version: 1, systems: { demo: { repos } }, ...marker };
  const folder = `intent/demo/${CHANGE}`;
  repo(join(ws, 'master'), {
    '.hoshin.json': JSON.stringify(fullMarker, null, 2) + '\n',
    [`${folder}/intent.md`]: INTENT,
    ...(built ? { [`${folder}/spec.md`]: example('spec.md'), [`${folder}/plan.md`]: example('plan.md'), [`${folder}/build/app.md`]: buildLog(['T1', 'T2', 'T3']) } : {}),
    ...masterFiles,
  });
  for (const name of repos) repo(join(ws, name), { 'README.md': `# ${name}\n` });
  if (built) git(join(ws, 'app'), 'checkout', '-q', '-b', `change/${CHANGE}`);
  return { ws, master: join(ws, 'master'), app: join(ws, 'app') };
}

/** Commits files in a repository. */
export function commit(dir, files, message = 'docs(demo): change') {
  for (const [name, text] of Object.entries(files)) write(join(dir, name), text);
  git(dir, 'add', '.');
  git(dir, 'commit', '-q', '-m', message, '-m', 'Generated-By: hoshin');
  return git(dir, 'rev-parse', 'HEAD');
}

/** The JSON lines of a program's output. */
function jsonLines(stdout) {
  return stdout.replace(/\r\n/g, '\n').split('\n').filter(Boolean).flatMap((l) => {
    try {
      return [JSON.parse(l)];
    } catch {
      return [];
    }
  });
}

/** Runs `dotnet hoshin.dll`; JSON output is parsed into `json` (the last line) and `lines`. */
export function run(cwd, args, extraEnv = {}) {
  const r = spawnSync(dotnet, [program, ...args], { cwd, encoding: 'utf8', env: { ...env, ...extraEnv } });
  const lines = jsonLines(r.stdout ?? '');
  return { status: r.status, stdout: r.stdout ?? '', stderr: r.stderr ?? '', lines, json: lines[lines.length - 1] };
}

/** Runs `dotnet hoshin.dll` with every git invocation counted via HOSHIN_GIT_COUNT_FILE. */
export function runCounted(cwd, args) {
  const dir = tempDir('hoshin-count-');
  const file = join(dir, 'count.txt');
  try {
    const r = spawnSync(dotnet, [program, ...args], {
      cwd,
      encoding: 'utf8',
      env: { ...env, HOSHIN_GIT_COUNT_FILE: file },
    });
    const gitCount = existsSync(file)
      ? readFileSync(file, 'utf8').split('\n').filter(Boolean).reduce((sum, n) => sum + Number(n), 0)
      : 0;
    const lines = jsonLines(r.stdout ?? '');
    return { status: r.status, json: lines[lines.length - 1], stderr: r.stderr ?? '', gitCount };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

/** A hook entry that runs Node with a script. */
export const nodeHook = (id, script, rest = {}) => ({ id, run: [node, '-e', script], ...rest });
