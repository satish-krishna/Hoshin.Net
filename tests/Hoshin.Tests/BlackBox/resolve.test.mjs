// `hoshin resolve --json`: where the workspace and the master repository are.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, realpathSync, symlinkSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { dotnet, env, program } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const real = (path) => realpathSync.native(path);

function git(cwd, ...args) {
  const r = spawnSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.com', ...args], { cwd, encoding: 'utf8', env });
  assert.equal(r.status, 0, `git ${args.join(' ')}: ${r.stderr}`);
  return r.stdout.trim();
}

function repo(path, { marker = false, separateGitDir } = {}) {
  mkdirSync(path, { recursive: true });
  git(path, 'init', '-q', ...(separateGitDir ? [`--separate-git-dir=${separateGitDir}`] : []));
  if (marker) writeFileSync(join(path, '.hoshin.json'), JSON.stringify({ version: 1, systems: { demo: { repos: ['app'] } } }));
  writeFileSync(join(path, 'README.md'), '# repo\n');
  git(path, 'add', '.');
  git(path, 'commit', '-q', '-m', 'chore: initial');
  return real(path);
}

function workspace(options = {}) {
  const root = real(tempDir('hoshin-resolve-'));
  const ws = join(root, 'ws');
  const master = repo(join(ws, 'master'), { marker: true, ...options });
  const app = repo(join(ws, 'app'));
  return { root, ws: real(ws), master, app };
}

function copy(master, root, name = 'change-a') {
  const path = join(root, 'copies', name);
  git(master, 'worktree', 'add', '-q', '-b', `change/${name}`, path);
  return real(path);
}

function run(cwd, ...args) {
  const r = spawnSync(dotnet, [program, 'resolve', '--json', ...args], {
    cwd,
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', GIT_CONFIG_NOSYSTEM: '1' },
    encoding: 'utf8',
  });
  let out = null;
  try {
    out = r.stdout ? JSON.parse(r.stdout) : null;
  } catch {
    assert.fail(`not JSON: ${r.stdout}`);
  }
  return { status: r.status, out, stderr: r.stderr };
}

test('no options inside a product repository gives the sibling master, as today', () => {
  const w = workspace();
  const r = run(w.app);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: w.ws, master: w.master, masterIsCopy: false, problems: [], layout: 'workspace' });
});

test('no options inside the master repository, or a folder in it, gives the same answer', () => {
  const w = workspace();
  mkdirSync(join(w.master, 'intent'));
  for (const cwd of [w.master, join(w.master, 'intent')]) {
    const r = run(cwd);
    assert.equal(r.status, 0, r.stderr);
    assert.deepEqual(r.out, { workspace: w.ws, master: w.master, masterIsCopy: false, problems: [], layout: 'workspace' });
  }
});

test('no options in the workspace folder itself treats it as the workspace, as today', () => {
  const w = workspace();
  const r = run(w.ws);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: w.ws, master: w.master, masterIsCopy: false, problems: [], layout: 'workspace' });
});

test('the workspace folder is the workspace even when it sits inside another repository, such as a home folder under git', () => {
  const w = workspace();
  git(w.root, 'init', '-q');
  const r = run(w.ws);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: w.ws, master: w.master, masterIsCopy: false, problems: [], layout: 'workspace' });
  const fromApp = run(w.app);
  assert.deepEqual(fromApp.out, { workspace: w.ws, master: w.master, masterIsCopy: false, problems: [], layout: 'workspace' });
});

test('--master from the workspace folder finds that workspace even when it sits inside another repository, such as a home folder under git', () => {
  const w = workspace();
  git(w.root, 'init', '-q');
  const c = copy(w.master, w.ws, 'change-b');
  const r = run(w.ws, '--master', c);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: w.ws, master: c, masterIsCopy: true, problems: [], layout: 'workspace' });
  const main = run(w.ws, '--master', w.master);
  assert.deepEqual(main.out, { workspace: w.ws, master: w.master, masterIsCopy: false, problems: [], layout: 'workspace' });
});

test("no options gives the engineer's own checkout even when copies exist outside the workspace", () => {
  const w = workspace();
  copy(w.master, w.root);
  const r = run(w.app);
  assert.equal(r.status, 0, r.stderr);
  assert.equal(r.out.master, w.master);
  assert.equal(r.out.masterIsCopy, false);
});

test('a workspace with no master is reported with the message the skills stop with', () => {
  const root = real(tempDir('hoshin-resolve-'));
  const app = repo(join(root, 'ws', 'app'));
  const r = run(app);
  assert.equal(r.status, 1);
  assert.equal(r.out.workspace, real(join(root, 'ws')));
  assert.equal(r.out.master, null);
  assert.deepEqual(r.out.problems, [
    `No master repository found in \`${real(join(root, 'ws'))}\`. An engineer needs to run the hoshin setup skill first.`,
  ]);
});

test('two unrelated master repositories in one workspace are refused, as today', () => {
  const w = workspace();
  const other = repo(join(w.ws, 'other'), { marker: true });
  const r = run(w.app);
  assert.equal(r.status, 1);
  assert.equal(r.out.master, null);
  assert.equal(r.out.problems.length, 1);
  assert.match(r.out.problems[0], /^Found more than one master repository \(/);
  assert.ok(r.out.problems[0].includes(w.master) && r.out.problems[0].includes(other), r.out.problems[0]);
});

test('--master naming a copy, from a product repository, gives the copy and the workspace of its main checkout', () => {
  const w = workspace();
  const c = copy(w.master, w.root);
  const r = run(w.app, '--master', c);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: w.ws, master: c, masterIsCopy: true, problems: [], layout: 'workspace' });
});

test('a session inside a copy resolves the copy and the workspace with no options', () => {
  const w = workspace();
  const c = copy(w.master, w.root);
  mkdirSync(join(c, 'intent'), { recursive: true });
  for (const cwd of [c, join(c, 'intent')]) {
    const r = run(cwd);
    assert.equal(r.status, 0, r.stderr);
    assert.deepEqual(r.out, { workspace: w.ws, master: c, masterIsCopy: true, problems: [], layout: 'workspace' });
  }
});

test('--master naming the main checkout itself is not a copy', () => {
  const w = workspace();
  const r = run(w.app, '--master', w.master);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: w.ws, master: w.master, masterIsCopy: false, problems: [], layout: 'workspace' });
});

test("--master naming a repository that is not a worktree of the workspace's master is refused", () => {
  const w = workspace();
  const stranger = repo(join(w.root, 'elsewhere', 'master'), { marker: true });
  const r = run(w.app, '--master', stranger, '--workspace', w.ws);
  assert.equal(r.status, 1);
  assert.equal(r.out.master, null);
  assert.deepEqual(r.out.problems, [
    `\`${stranger}\` is not a worktree of the master repository in \`${w.ws}\` (\`${w.master}\`); they are different git repositories.`,
  ]);
});

test("from a product repository, --master is checked against that repository's workspace", () => {
  const w = workspace();
  const stranger = repo(join(w.root, 'elsewhere', 'master'), { marker: true });
  const strangerCopy = copy(stranger, w.root, 'stranger-copy');
  const r = run(w.app, '--master', strangerCopy);
  assert.equal(r.status, 1);
  assert.equal(r.out.workspace, w.ws);
  assert.equal(r.out.master, null);
  assert.deepEqual(r.out.problems, [
    `\`${strangerCopy}\` is not a worktree of the master repository in \`${w.ws}\` (\`${w.master}\`); they are different git repositories.`,
  ]);
});

test('from a product repository, --master finds the workspace even when git cannot locate the main checkout', () => {
  const root = real(tempDir('hoshin-resolve-'));
  const ws = join(root, 'ws');
  const master = repo(join(ws, 'master'), { marker: true, separateGitDir: join(root, 'master.git') });
  const app = repo(join(ws, 'app'));
  const c = copy(master, root);
  const r = run(app, '--master', c);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: real(ws), master: c, masterIsCopy: true, problems: [], layout: 'workspace' });
});

test('--master naming a folder with no .hoshin.json is refused', () => {
  const w = workspace();
  const r = run(w.app, '--master', w.app);
  assert.equal(r.status, 1);
  assert.equal(r.out.master, null);
  assert.deepEqual(r.out.problems, [`\`${w.app}\` holds no .hoshin.json, so it is not a master repository or a copy of one.`]);
});

test('--master naming a folder that is not the root of a git repository is refused', () => {
  const w = workspace();
  const plain = join(w.root, 'plain');
  mkdirSync(plain);
  writeFileSync(join(plain, '.hoshin.json'), '{}');
  mkdirSync(join(w.master, 'intent'));
  for (const [path, shown] of [[plain, real(plain)], [join(w.master, 'intent'), real(join(w.master, 'intent'))]]) {
    const r = run(w.app, '--master', path);
    assert.equal(r.status, 1, path);
    assert.deepEqual(r.out.problems, [`\`${shown}\` is not the root of a git repository.`]);
  }
});

test('--master naming a folder that does not exist is refused', () => {
  const w = workspace();
  const missing = join(w.root, 'nowhere');
  const r = run(w.app, '--master', missing);
  assert.equal(r.status, 1);
  assert.deepEqual(r.out.problems, [`\`${resolve(missing)}\` does not exist.`]);
});

test('a copy whose main checkout git cannot locate asks for --workspace, and resolves with it', () => {
  const root = real(tempDir('hoshin-resolve-'));
  const ws = join(root, 'ws');
  const master = repo(join(ws, 'master'), { marker: true, separateGitDir: join(root, 'master.git') });
  repo(join(ws, 'app'));
  const c = copy(master, root);
  const without = run(c);
  assert.equal(without.status, 1);
  assert.equal(without.out.master, null);
  assert.deepEqual(without.out.problems, [
    `Cannot tell where the main checkout of \`${c}\` is: its git directory, \`${real(join(root, 'master.git'))}\`, is kept apart from any checkout. Pass --workspace <path>.`,
  ]);
  const withWs = run(c, '--workspace', ws);
  assert.equal(withWs.status, 0, withWs.stderr);
  assert.deepEqual(withWs.out, { workspace: real(ws), master: c, masterIsCopy: true, problems: [], layout: 'workspace' });
});

test('a git directory kept apart is found through core.worktree when it is set', () => {
  const root = real(tempDir('hoshin-resolve-'));
  const ws = join(root, 'ws');
  const master = repo(join(ws, 'master'), { marker: true, separateGitDir: join(root, 'master.git') });
  git(master, 'config', 'core.worktree', master);
  const c = copy(master, root);
  const r = run(c);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: real(ws), master: c, masterIsCopy: true, problems: [], layout: 'workspace' });
});

test('a copy beside the master in the workspace is the same repository, not a second master', () => {
  const w = workspace();
  const sibling = join(w.ws, 'master-copy');
  git(w.master, 'worktree', 'add', '-q', '-b', 'change/sibling', sibling);
  const r = run(real(sibling));
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: w.ws, master: real(sibling), masterIsCopy: true, problems: [], layout: 'workspace' });
});

test('--workspace naming a folder that does not exist is refused', () => {
  const w = workspace();
  const c = copy(w.master, w.root);
  const missing = join(w.root, 'nowhere');
  const r = run(w.app, '--master', c, '--workspace', missing);
  assert.equal(r.status, 1);
  assert.deepEqual(r.out.problems, [`\`${resolve(missing)}\` does not exist.`]);
});

test('--workspace alone replaces the parent folder of the current repository', () => {
  const w = workspace();
  const elsewhere = repo(join(w.root, 'loose'));
  const r = run(elsewhere, '--workspace', w.ws);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: w.ws, master: w.master, masterIsCopy: false, problems: [], layout: 'workspace' });
});

test('a master that is a symbolic link (a junction on Windows) in the workspace is found, as the old rule found it', () => {
  const root = real(tempDir('hoshin-resolve-'));
  const target = repo(join(root, 'elsewhere', 'master'), { marker: true });
  const ws = join(root, 'ws');
  const app = repo(join(ws, 'app'));
  symlinkSync(target, join(ws, 'master'), 'junction');
  const r = run(app);
  assert.equal(r.status, 0, r.stderr);
  assert.equal(r.out.workspace, real(ws));
  assert.equal(r.out.masterIsCopy, false);
  assert.equal(real(r.out.master), target);
});

test('a master that is a worktree of a bare repository resolves from inside itself, beside the bare repository', () => {
  const root = real(tempDir('hoshin-resolve-'));
  const ws = join(root, 'ws');
  const seed = repo(join(root, 'seed'), { marker: true });
  git(root, 'clone', '-q', '--bare', seed, join(ws, 'master.git'));
  git(join(ws, 'master.git'), 'worktree', 'add', '-q', join(ws, 'master'));
  repo(join(ws, 'app'));
  const master = real(join(ws, 'master'));
  for (const cwd of [master, real(join(ws, 'app'))]) {
    const r = run(cwd, ...(cwd === master ? [] : ['--master', master]));
    assert.equal(r.status, 0, `${cwd}: ${r.stderr} ${JSON.stringify(r.out)}`);
    assert.deepEqual(r.out, { workspace: real(ws), master, masterIsCopy: true, problems: [], layout: 'workspace' });
  }
});

test('an unknown option or a missing value is a usage error, exit 2', () => {
  const w = workspace();
  for (const args of [['--copy', w.master], ['--master'], ['--master', w.master, '--master', w.master]]) {
    const r = spawnSync(dotnet, [program, 'resolve', '--json', ...args], { cwd: w.app, encoding: 'utf8', env });
    assert.equal(r.status, 2, args.join(' '));
    assert.match(r.stderr, /usage:/);
  }
});
