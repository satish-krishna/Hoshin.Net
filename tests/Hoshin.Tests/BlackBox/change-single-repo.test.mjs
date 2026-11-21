// change start, rename and remove in the single-repo layout: one repository, so one branch and one copy, and the
// copy lives beside the repository, never inside it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync } from 'node:fs';
import { join, relative } from 'node:path';
import { commit, git, run } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';

const NAME = '2026-11-02-wave';
const start = (s) => run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', NAME, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Wave']);
const act = (s, verb, ...rest) => run(s.repo, ['change', verb, '--json', '--system', 'demo', '--change', NAME, ...rest]);

test('start makes the copy beside the repository and leaves the main checkout alone', () => {
  const s = singleRepo();
  const before = git(s.repo, 'rev-parse', 'HEAD');
  const r = start(s);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  const copy = r.json.details.copy;
  assert.equal(copy, join(s.workspace, '.hoshin', 'copies', NAME));
  assert.ok(relative(s.repo, copy).startsWith('..'), 'the copy is not inside the repository folder');
  assert.ok(existsSync(join(copy, 'apps', 'api', 'README.md')));
  assert.equal(git(s.repo, 'rev-parse', '--abbrev-ref', 'HEAD'), 'main');
  assert.equal(git(s.repo, 'rev-parse', 'HEAD'), before);
  assert.equal(git(s.repo, 'status', '--porcelain'), '');
  assert.equal(git(copy, 'rev-parse', '--abbrev-ref', 'HEAD'), `change/${NAME}`);
});

test('start refuses a branch name the repository already has, once', () => {
  const s = singleRepo();
  git(s.repo, 'branch', 'taken');
  const r = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', NAME, '--type', 'feat', '--scope', 'PROJ-1', '--branch', 'taken']);
  assert.notEqual(r.json.outcome, 'passed');
  assert.equal(JSON.stringify(r.json).split('taken').length > 1, true);
});

test('rename moves the one branch', () => {
  const s = singleRepo();
  const copy = start(s).json.details.copy;
  const r = act(s, 'rename', '--to', 'feature/wave');
  assert.deepEqual(r.json.problems, []);
  assert.deepEqual(r.json.renamed, ['master']);
  assert.equal(git(copy, 'rev-parse', '--abbrev-ref', 'HEAD'), 'feature/wave');
  assert.equal(git(s.repo, 'branch', '--list', `change/${NAME}`), '');
  assert.notEqual(git(s.repo, 'branch', '--list', 'feature/wave'), '');
});

test('remove --abandon refuses after a code commit on the branch, naming it', () => {
  const s = singleRepo();
  const copy = start(s).json.details.copy;
  commit(copy, { 'apps/api/new.txt': 'x' }, 'feat(PROJ-1): api work');
  const r = act(s, 'remove', '--abandon');
  assert.equal(r.json.problems.length, 1, JSON.stringify(r.json));
  assert.match(r.json.problems[0], /feat\(PROJ-1\): api work/);
  assert.ok(existsSync(copy));
});

test('remove --abandon drops an untouched change: the copy and the branch', () => {
  const s = singleRepo();
  const copy = start(s).json.details.copy;
  const r = act(s, 'remove', '--abandon');
  assert.deepEqual(r.json.problems, [], JSON.stringify(r.json));
  assert.equal(existsSync(copy), false);
  assert.equal(git(s.repo, 'branch', '--list', `change/${NAME}`), '');
  assert.equal(git(s.repo, 'rev-parse', '--abbrev-ref', 'HEAD'), 'main');
});

// An app name is not a repository: a sibling repository that happens to share an app's name is none of the change's.
function withSibling(s) {
  const sibling = join(s.workspace, 'api');
  mkdirSync(sibling, { recursive: true });
  git(sibling, 'init', '-q', '-b', 'main');
  commit(sibling, { 'a.txt': 'a' }, 'init');
  return sibling;
}

test('a sibling repository named like an app is left alone by start and rename', () => {
  const s = singleRepo();
  const sibling = withSibling(s);
  git(sibling, 'branch', `change/${NAME}`);
  const r = start(s);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  const renamed = act(s, 'rename', '--to', 'feature/wave');
  assert.deepEqual(renamed.json.renamed, ['master'], JSON.stringify(renamed.json));
  assert.notEqual(git(sibling, 'branch', '--list', `change/${NAME}`), '');
});

test('a sibling repository named like an app keeps its branch when the change is abandoned', () => {
  const s = singleRepo();
  const sibling = withSibling(s);
  git(sibling, 'branch', `change/${NAME}`);
  assert.equal(start(s).json.outcome, 'passed');
  const removed = act(s, 'remove', '--abandon');
  assert.deepEqual(removed.json.problems, [], JSON.stringify(removed.json));
  assert.notEqual(git(sibling, 'branch', '--list', `change/${NAME}`), '');
});
