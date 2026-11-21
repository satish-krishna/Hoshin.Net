// `resolve --json` in the single-repo layout: the repository is its own master, from its root, from an app folder and
// from a copy, and the default layout only gains a `layout` field.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, realpathSync } from 'node:fs';
import { join } from 'node:path';
import { dotnet, env, git, program, repo, workspace } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';

function run(cwd, ...args) {
  const r = spawnSync(dotnet, [program, 'resolve', '--json', ...args], { cwd, env, encoding: 'utf8' });
  return { status: r.status, out: r.stdout ? JSON.parse(r.stdout) : null, stderr: r.stderr };
}

test('from the repository root the repository is the master and its parent the workspace', () => {
  const s = singleRepo();
  const r = run(s.repo);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: s.workspace, master: s.repo, masterIsCopy: false, problems: [], layout: 'single-repo' });
});

test('from an app folder the answer is the same', () => {
  const s = singleRepo();
  const r = run(join(s.repo, 'apps', 'web'));
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out, { workspace: s.workspace, master: s.repo, masterIsCopy: false, problems: [], layout: 'single-repo' });
});

test('from a copy the copy is the master, and the workspace is the main checkout\'s parent', () => {
  const s = singleRepo();
  const copy = join(s.root, 'ws', '.hoshin', 'copies', 'x');
  s.git('worktree', 'add', '-q', '-b', 'change/x', copy);
  const path = realpathSync(copy);
  for (const cwd of [path, join(path, 'apps', 'api')]) {
    const r = run(cwd);
    assert.equal(r.status, 0, r.stderr);
    assert.deepEqual(r.out, { workspace: s.workspace, master: path, masterIsCopy: true, problems: [], layout: 'single-repo' });
  }
});

test('--master naming the repository or a copy resolves it from anywhere', () => {
  const s = singleRepo();
  const copy = join(s.root, 'copies', 'y');
  s.git('worktree', 'add', '-q', '-b', 'change/y', copy);
  const outside = realpathSync(s.root);
  const main = run(outside, '--master', s.repo);
  assert.deepEqual(main.out, { workspace: s.workspace, master: s.repo, masterIsCopy: false, problems: [], layout: 'single-repo' });
  const c = run(outside, '--master', copy);
  assert.deepEqual(c.out, { workspace: s.workspace, master: realpathSync(copy), masterIsCopy: true, problems: [], layout: 'single-repo' });
});

test('--workspace overrides the parent folder', () => {
  const s = singleRepo();
  const other = join(s.root, 'elsewhere');
  mkdirSync(other);
  const r = run(s.repo, '--workspace', other);
  assert.equal(r.out.workspace, realpathSync(other));
  assert.equal(r.out.master, s.repo);
  assert.equal(r.out.layout, 'single-repo');
});

test('another marker among the parent folder\'s children does not refuse', () => {
  const s = singleRepo();
  repo(join(s.workspace, 'other'), { 'README.md': '# other\n', '.hoshin.json': JSON.stringify({ version: 1, systems: { x: { repos: ['a'] } } }) });
  for (const cwd of [s.repo, join(s.repo, 'apps', 'web')]) {
    const r = run(cwd);
    assert.equal(r.status, 0, r.stderr);
    assert.deepEqual(r.out.problems, []);
    assert.equal(r.out.master, s.repo);
  }
});

test('a session in the parent folder keeps the default rules', () => {
  const s = singleRepo();
  const r = run(s.workspace);
  assert.equal(r.status, 0, r.stderr);
  assert.equal(r.out.master, s.repo);
  assert.equal(r.out.layout, 'single-repo');
});

test('the default layout prints layout workspace and nothing else changes', () => {
  const w = workspace();
  const r = run(w.app);
  assert.deepEqual(r.out, { workspace: w.ws, master: w.master, masterIsCopy: false, problems: [], layout: 'workspace' });
  const empty = join(w.ws, 'empty');
  mkdirSync(empty);
  const refusal = run(realpathSync(empty));
  assert.equal(refusal.status, 1);
  assert.equal(refusal.out.layout, 'workspace');
  assert.equal(Object.keys(r.out).at(-1), 'layout');
});
