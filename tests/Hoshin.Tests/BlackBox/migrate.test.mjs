// `migrate`: moves a workspace on this machine from ai-sdlc's names to hoshin's. Each case builds a real workspace
// whose names are the old ones (marker, copies folder, state folders, instructions block), with a real change copy.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { CHANGE, dotnet, env, git, program, run, workspace, write } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';
import { tempDir } from './helpers/temp.mjs';

const migrate = (cwd, ...args) => run(cwd, ['migrate', '--json', ...args]);
const real = (p) => realpathSync(p);
const norm = (p) => p.replaceAll('\\', '/');
const same = (a, b) => assert.equal(norm(real(a)), norm(real(b)));
const read = (p) => readFileSync(p, 'utf8');
const commonOf = (dir) => real(resolve(dir, git(dir, 'rev-parse', '--git-common-dir')));
const gitDirOf = (dir) => real(resolve(dir, git(dir, 'rev-parse', '--git-dir')));
const OLD_START = '<!-- ai-sdlc:start -->';
const OLD_END = '<!-- ai-sdlc:end -->';

/** Puts a state file at `<gitDir>/<folder>/<name>`. */
function stateFile(gitDir, folder, name, text = '{}\n') {
  write(join(gitDir, folder, name), text);
}

/** Renames the marker in a checkout the way ai-sdlc's names had it, and commits. */
function oldMarker(dir) {
  git(dir, 'mv', '.hoshin.json', '.ai-sdlc.json');
  git(dir, 'commit', '-q', '-m', 'chore(demo): old marker');
}

/**
 * A workspace as ai-sdlc 1.4.0 left it: master and app, the marker `.ai-sdlc.json`, one change copy under
 * `.ai-sdlc/copies`, ledger, baseline and active-build files under `ai-sdlc`, and the workspace instructions block
 * between the ai-sdlc markers. With `markerRenamed` the marker was already renamed (and pulled) on this machine.
 */
function legacy({ marker, markerRenamed = false, instructions = true, copy = true } = {}) {
  const w = workspace(marker ? { marker } : {});
  if (instructions) {
    assert.equal(run(w.master, ['instructions', '--json', '--write']).status, 0);
    git(w.master, 'add', 'intent/workspace.md');
    git(w.master, 'commit', '-q', '-m', 'chore(demo): instructions');
    const claude = join(w.ws, 'CLAUDE.md');
    writeFileSync(claude, `# Mine\n\n${read(claude).replace('<!-- hoshin:start -->', OLD_START).replace('<!-- hoshin:end -->', OLD_END)}`);
  }
  if (!markerRenamed) oldMarker(w.master);
  const folder = join(w.ws, '.ai-sdlc', 'copies', CHANGE);
  if (copy) {
    mkdirSync(join(w.ws, '.ai-sdlc', 'copies'), { recursive: true });
    git(w.master, 'worktree', 'add', '-q', '-b', `change/${CHANGE}`, folder, 'main');
  }
  const common = commonOf(w.master);
  stateFile(common, 'ai-sdlc', `demo/${CHANGE}/app.json`);
  stateFile(common, 'ai-sdlc', 'demo/.baselines/app.json');
  stateFile(commonOf(w.app), 'ai-sdlc', 'active-build.json');
  if (copy) stateFile(gitDirOf(folder), 'ai-sdlc', 'active-build.json');
  return { ...w, copy: folder, newCopy: join(w.ws, '.hoshin', 'copies', CHANGE), common, appCommon: commonOf(w.app) };
}

/** What a run must not change when it refuses or only looks. */
function snapshot(w) {
  const tree = (dir) => (existsSync(dir) ? readdirSync(dir, { recursive: true }).map(norm).sort().join('|') : '-');
  return JSON.stringify({
    master: git(w.master, 'rev-parse', 'HEAD'),
    branches: git(w.master, 'branch', '-a', '-v'),
    worktrees: norm(git(w.master, 'worktree', 'list')),
    status: git(w.master, 'status', '--porcelain'),
    claude: existsSync(join(w.ws, 'CLAUDE.md')) ? read(join(w.ws, 'CLAUDE.md')) : null,
    common: tree(w.common),
    app: tree(w.appCommon),
    old: tree(join(w.ws, '.ai-sdlc')),
    fresh: tree(join(w.ws, '.hoshin')),
    markerFiles: [w.master, w.copy].map((d) => `${existsSync(join(d, '.ai-sdlc.json'))}${existsSync(join(d, '.hoshin.json'))}`).join(','),
  });
}

const statuses = (r) => r.json.steps.map((s) => `${s.step}:${s.status}`);

test('a dry run changes nothing and reports what it would do', () => {
  const w = legacy({ marker: { systems: { demo: { repos: ['app'], commitScope: 'greeter' } } } });
  const before = snapshot(w);
  const r = migrate(w.ws);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(snapshot(w), before);
  same(r.json.workspace, w.ws);
  same(r.json.master, w.master);
  assert.equal(r.json.layout, 'workspace');
  assert.equal(r.json.write, false);
  assert.deepEqual(r.json.problems, []);
  assert.deepEqual(r.json.kept, []);
  assert.deepEqual(statuses(r), ['marker:would', 'marker:would', 'copy:would', 'state:would', 'state:would', 'state:would', 'instructions:would']);
  const [m, c, copy] = r.json.steps;
  same(m.path, join(w.master, '.ai-sdlc.json'));
  assert.equal(norm(m.to), norm(join(real(w.master), '.hoshin.json')));
  assert.equal(norm(c.to), norm(join(real(w.copy), '.hoshin.json')));
  assert.equal(norm(copy.to), norm(join(real(w.ws), '.hoshin', 'copies', CHANGE)));
  assert.equal(copy.detail, null);
  assert.match(r.json.manual.join('\n'), /push the marker commit on main, change\/2026-11-01-greeting/);
  assert.match(r.json.manual.join('\n'), /hoshin@hoshin.*ai-sdlc@ai-sdlc/);
});

test('--write does every step, and the migrated workspace works with the program', () => {
  const w = legacy({ marker: { systems: { demo: { repos: ['app'], commitScope: 'greeter' } } } });
  const r = migrate(w.ws, '--write');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(statuses(r), ['marker:done', 'marker:done', 'copy:done', 'state:done', 'state:done', 'state:done', 'instructions:done']);
  assert.deepEqual(r.json.problems, []);
  assert.equal(r.json.write, true);

  // The marker commits: master's carries the system's scope, the copy's the change's; each names only the two paths.
  for (const [dir, scope] of [[w.master, 'greeter'], [w.newCopy, 'demo']]) {
    assert.equal(git(dir, 'log', '-1', '--format=%s%n%b').trim(), `chore(${scope}): rename the marker to .hoshin.json\nGenerated-By: hoshin`);
    assert.deepEqual(git(dir, 'show', '--no-renames', '--name-status', '--format=', 'HEAD').split('\n').map((l) => l.replace(/\s+/g, ' ')).sort(), ['A .hoshin.json', 'D .ai-sdlc.json']);
    assert.equal(git(dir, 'status', '--porcelain', '--untracked-files=no'), '');
    assert.equal(existsSync(join(dir, '.ai-sdlc.json')), false);
    assert.equal(existsSync(join(dir, '.hoshin.json')), true);
  }
  assert.equal(git(w.master, 'branch', '--show-current'), 'main');
  assert.equal(git(w.newCopy, 'branch', '--show-current'), `change/${CHANGE}`);

  // The copy moved, and git knows it there.
  assert.equal(existsSync(w.copy), false);
  assert.equal(existsSync(join(w.ws, '.ai-sdlc')), false);
  assert.ok(norm(git(w.master, 'worktree', 'list', '--porcelain')).includes(`worktree ${norm(real(w.newCopy))}`));

  // The state moved.
  for (const [dir, name] of [[w.common, `demo/${CHANGE}/app.json`], [w.common, 'demo/.baselines/app.json'], [w.appCommon, 'active-build.json'], [gitDirOf(w.newCopy), 'active-build.json']]) {
    assert.equal(existsSync(join(dir, 'hoshin', name)), true, `${dir} ${name}`);
    assert.equal(existsSync(join(dir, 'ai-sdlc')), false);
  }

  // The instructions block has the new markers, in the place of the old, and the text around it is the engineer's.
  const claude = read(join(w.ws, 'CLAUDE.md'));
  assert.ok(claude.startsWith('# Mine\n\n<!-- hoshin:start -->\n'));
  assert.ok(claude.trimEnd().endsWith('<!-- hoshin:end -->'));
  assert.doesNotMatch(claude, /ai-sdlc/);
  assert.equal(run(w.master, ['instructions', '--json']).json.claudeMd, 'current');

  // Every command works on the migrated workspace, and shows the change.
  const resolved = run(w.ws, ['resolve', '--json']);
  assert.equal(resolved.status, 0, resolved.stdout);
  same(resolved.json.master, w.master);
  const state = run(w.master, ['state', '--json']);
  assert.equal(state.status, 0, state.stdout + state.stderr);
  assert.ok(state.json.changes.some((c) => c.change === CHANGE));
  const inCopy = run(w.newCopy, ['resolve', '--json']);
  assert.equal(inCopy.json.masterIsCopy, true);
  same(inCopy.json.master, w.newCopy);
});

test('a second --write finds everything current, and the dry run says the same', () => {
  const w = legacy();
  assert.equal(migrate(w.ws, '--write').status, 0);
  for (const args of [['--write'], []]) {
    const r = migrate(w.ws, ...args);
    assert.equal(r.status, 0, r.stdout);
    assert.deepEqual(statuses(r), ['marker:current', 'marker:current', 'state:current', 'state:current', 'state:current', 'instructions:current']);
    assert.deepEqual(r.json.problems, []);
    assert.match(r.json.manual[0], /^push any marker commit not yet pushed/);
  }
});

test('on a second machine the marker is already renamed, and only the local steps are taken', () => {
  const w = legacy({ markerRenamed: true });
  const before = git(w.master, 'rev-parse', 'HEAD');
  const r = migrate(w.ws, '--write');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(statuses(r), ['marker:current', 'marker:current', 'copy:done', 'state:done', 'state:done', 'state:done', 'instructions:done']);
  assert.equal(git(w.master, 'rev-parse', 'HEAD'), before, 'a commit was made');
  assert.equal(existsSync(join(w.newCopy, '.hoshin.json')), true);
  assert.match(r.json.manual[0], /^push any marker commit/);
});

test('a checkout that tracks neither marker has nothing to rename', () => {
  const w = legacy();
  git(w.master, 'branch', 'bare');
  const other = join(w.ws, 'other');
  git(w.master, 'worktree', 'add', '-q', other, 'bare');
  git(other, 'rm', '-q', '.ai-sdlc.json');
  git(other, 'commit', '-q', '-m', 'chore(demo): no marker');
  const r = migrate(w.ws);
  const bare = r.json.steps.find((s) => norm(s.path).startsWith(norm(real(other))));
  assert.equal(bare.status, 'current');
  assert.match(bare.detail, /is not tracked here/);
});

test('instructions it cannot write stay as they are and are named as manual work', () => {
  const w = legacy({ instructions: false });
  const claude = join(w.ws, 'CLAUDE.md');
  const text = `# Mine\n\n${OLD_START}\nold text\n${OLD_END}\n`;
  writeFileSync(claude, text);
  const r = migrate(w.ws, '--write');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  const step = r.json.steps.find((s) => s.step === 'instructions');
  assert.equal(step.status, 'manual');
  assert.match(step.detail, /workspace\.md is missing, so the block was left as it is; run the hoshin setup skill with `--instructions`/);
  assert.equal(read(claude), text);
  assert.ok(r.json.manual.some((m) => m.startsWith('instructions: ') && m.includes('--instructions')));
  assert.equal(r.json.steps.filter((s) => s.status === 'done').length, 6, 'the other steps were not taken');
});

test('a workspace in the home folder keeps its block, whatever the instructions say', () => {
  const w = legacy();
  const r = spawnSync(dotnet, [program, 'migrate', '--json'], { cwd: w.ws, encoding: 'utf8', env: { ...env, HOME: w.ws, USERPROFILE: w.ws } });
  const step = JSON.parse(r.stdout.trim().split('\n').pop()).steps.find((s) => s.step === 'instructions');
  assert.equal(step.status, 'manual');
  assert.match(step.detail, /the workspace is the home folder/);
});

test('entries left in the old copies folder stay, and are reported as kept', () => {
  const w = legacy();
  write(join(w.ws, '.ai-sdlc', 'copies', 'notes.txt'), 'mine');
  write(join(w.ws, '.ai-sdlc', 'other.txt'), 'mine');
  const dry = migrate(w.ws);
  assert.deepEqual(dry.json.kept.map(norm).sort(), [join(w.ws, '.ai-sdlc', 'copies', 'notes.txt'), join(w.ws, '.ai-sdlc', 'other.txt')].map(norm).sort());
  const r = migrate(w.ws, '--write');
  assert.equal(r.status, 0, r.stdout);
  assert.deepEqual(r.json.kept.map(norm).sort(), dry.json.kept.map(norm).sort());
  assert.equal(read(join(w.ws, '.ai-sdlc', 'copies', 'notes.txt')), 'mine');
  assert.equal(existsSync(w.newCopy), true);
  const again = migrate(w.ws, '--write');
  assert.equal(again.json.kept.length, 2);
  assert.equal(again.status, 0);
});

test('each refusal changes nothing, names the step, and exits 1', () => {
  const rows = [
    ['both marker files exist', (w) => write(join(w.master, '.hoshin.json'), read(join(w.master, '.ai-sdlc.json'))), /marker .*: both \.ai-sdlc\.json and \.hoshin\.json exist/],
    ['both marker files are tracked', (w) => { write(join(w.copy, '.hoshin.json'), read(join(w.copy, '.ai-sdlc.json'))); git(w.copy, 'add', '.hoshin.json'); git(w.copy, 'commit', '-q', '-m', 'x'); rmSync(join(w.copy, '.hoshin.json')); }, /both \.ai-sdlc\.json and \.hoshin\.json are tracked/],
    ['the marker has uncommitted changes', (w) => writeFileSync(join(w.master, '.ai-sdlc.json'), read(join(w.master, '.ai-sdlc.json')) + '\n'), /\.ai-sdlc\.json has uncommitted changes/],
    ['the index has staged changes', (w) => { write(join(w.master, 'staged.txt'), 'x'); git(w.master, 'add', 'staged.txt'); }, /the index has staged changes/],
    ['a merge is in progress', (w) => writeFileSync(join(gitDirOf(w.master), 'MERGE_HEAD'), git(w.master, 'rev-parse', 'HEAD') + '\n'), /a merge or rebase is in progress/],
    ['a rebase is in progress', (w) => mkdirSync(join(gitDirOf(w.copy), 'rebase-merge')), /a merge or rebase is in progress/],
    ['HEAD is detached', (w) => git(w.copy, 'checkout', '-q', '--detach'), /HEAD is detached/],
    ['the new copy folder exists', (w) => mkdirSync(w.newCopy, { recursive: true }), /copy .*already exists/],
    ['both state folders exist', (w) => stateFile(w.appCommon, 'hoshin', 'x.json'), /state .*both ai-sdlc and hoshin exist/],
    ['both instructions blocks exist', (w) => writeFileSync(join(w.ws, 'CLAUDE.md'), read(join(w.ws, 'CLAUDE.md')) + '<!-- hoshin:start -->\n<!-- hoshin:end -->\n'), /instructions .*holds a block between the ai-sdlc markers and one between the hoshin markers/],
    ['a start marker has no end marker', (w) => writeFileSync(join(w.ws, 'CLAUDE.md'), read(join(w.ws, 'CLAUDE.md')).replace(OLD_END, '')), /instructions .*without its end marker/],
  ];
  for (const [what, setup, problem] of rows) {
    const w = legacy();
    setup(w);
    const before = snapshot(w);
    for (const args of [['--write'], []]) {
      const r = migrate(w.ws, ...args);
      assert.equal(r.status, 1, `${what}: ${r.stdout}${r.stderr}`);
      assert.equal(r.json.problems.length, 1, `${what}: ${r.json.problems}`);
      assert.match(r.json.problems[0], problem, what);
      assert.ok(r.json.steps.some((s) => s.status === 'refused'), what);
      assert.equal(snapshot(w), before, `${what}: something changed`);
    }
  }
});

test('every refusal is listed, not only the first', () => {
  const w = legacy();
  write(join(w.master, 'staged.txt'), 'x');
  git(w.master, 'add', 'staged.txt');
  git(w.copy, 'checkout', '-q', '--detach');
  const r = migrate(w.ws, '--write');
  assert.equal(r.status, 1);
  assert.equal(r.json.problems.length, 2);
});

test('a step that fails midway stops there, says why, and a rerun finishes the work', () => {
  const w = legacy();
  git(w.master, 'worktree', 'lock', w.copy);
  const r = migrate(w.ws, '--write');
  assert.equal(r.status, 1, r.stdout);
  assert.deepEqual(statuses(r), ['marker:done', 'marker:done', 'copy:refused']);
  const failed = r.json.steps[2];
  assert.match(failed.detail, /locked/);
  assert.equal(r.json.problems.length, 1);
  assert.match(r.json.problems[0], /^copy .*locked/);
  assert.equal(existsSync(w.copy), true);
  git(w.master, 'worktree', 'unlock', w.copy);
  const again = migrate(w.ws, '--write');
  assert.equal(again.status, 0, again.stdout);
  assert.deepEqual(statuses(again), ['marker:current', 'marker:current', 'copy:done', 'state:done', 'state:done', 'state:done', 'instructions:done']);
});

test('a commit the repository refuses is undone, and reported as the step\'s failure', () => {
  const w = legacy({ copy: false, instructions: false });
  write(join(gitDirOf(w.master), 'hooks', 'pre-commit'), '#!/bin/sh\necho "no commits today" >&2\nexit 1\n');
  spawnSync('chmod', ['+x', join(gitDirOf(w.master), 'hooks', 'pre-commit')]);
  const r = migrate(w.ws, '--write');
  assert.equal(r.status, 1, r.stdout);
  assert.deepEqual(statuses(r), ['marker:refused']);
  assert.match(r.json.steps[0].detail, /no commits today/);
  assert.equal(existsSync(join(w.master, '.ai-sdlc.json')), true);
  assert.equal(existsSync(join(w.master, '.hoshin.json')), false);
  assert.equal(git(w.master, 'status', '--porcelain'), '');
});

test('with no marker of either name it cannot run', () => {
  const folder = realpathSync(tempDir('hoshin-none-'));
  const r = spawnSync(dotnet, [program, 'migrate', '--json'], { cwd: folder, encoding: 'utf8', env });
  assert.equal(r.status, 2);
  assert.match(r.stderr, /No master repository found/);
  assert.equal(spawnSync(dotnet, [program, 'migrate'], { cwd: folder, encoding: 'utf8', env }).status, 2);
});

test('--master and --workspace mean what they mean for resolve', () => {
  const w = legacy();
  const elsewhere = realpathSync(tempDir('hoshin-else-'));
  const r = migrate(elsewhere, '--master', w.master, '--workspace', w.ws);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  same(r.json.master, w.master);
  const fromCopy = migrate(elsewhere, '--master', w.copy);
  same(fromCopy.json.master, w.copy);
  assert.equal(fromCopy.json.steps.length, r.json.steps.length);
});

test('the single-repo layout: the repository is its own master, and its CLAUDE.md holds the block', () => {
  const s = singleRepo();
  assert.equal(run(s.repo, ['instructions', '--json', '--write']).status, 0);
  s.git('add', '.');
  s.git('commit', '-q', '-m', 'chore(demo): instructions');
  const claude = join(s.repo, 'CLAUDE.md');
  writeFileSync(claude, read(claude).replace('<!-- hoshin:start -->', OLD_START).replace('<!-- hoshin:end -->', OLD_END));
  s.git('add', '.');
  s.git('commit', '-q', '-m', 'chore(demo): old block');
  oldMarker(s.repo);
  const copy = join(s.workspace, '.ai-sdlc', 'copies', 'c1');
  mkdirSync(join(s.workspace, '.ai-sdlc', 'copies'), { recursive: true });
  s.git('worktree', 'add', '-q', '-b', 'change/c1', copy, 'main');
  stateFile(commonOf(s.repo), 'ai-sdlc', 'demo/c1/mono.json');
  stateFile(gitDirOf(copy), 'ai-sdlc', 'active-build.json');

  const r = migrate(s.repo, '--write');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.layout, 'single-repo');
  assert.deepEqual(statuses(r), ['marker:done', 'marker:done', 'copy:done', 'state:done', 'state:done', 'instructions:done']);
  assert.equal(git(s.repo, 'log', '-1', '--format=%s'), 'chore(demo): rename the marker to .hoshin.json');
  assert.equal(existsSync(join(s.workspace, '.hoshin', 'copies', 'c1', '.hoshin.json')), true);
  assert.equal(existsSync(join(commonOf(s.repo), 'hoshin', 'demo', 'c1', 'mono.json')), true);
  assert.equal(existsSync(join(s.workspace, '.ai-sdlc')), false);
  assert.ok(read(claude).includes('<!-- hoshin:start -->') && !read(claude).includes('ai-sdlc'));
  assert.ok(r.json.manual.some((m) => m.includes('commit CLAUDE.md')));
  assert.equal(existsSync(join(s.workspace, 'CLAUDE.md')), false);
  const resolved = run(join(s.workspace, '.hoshin', 'copies', 'c1'), ['resolve', '--json']);
  assert.equal(resolved.json.layout, 'single-repo');
  assert.equal(migrate(s.repo).json.steps.every((x) => x.status === 'current'), true);
});
