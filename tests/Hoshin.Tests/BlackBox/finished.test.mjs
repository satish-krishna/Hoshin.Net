import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { CHANGE, commit, git, run, workspace, write } from './helpers/workspace.mjs';
import { intent } from './helpers/documents.mjs';

// `finished` in `state --json`: null (open), "rejected", or "merged". Every case builds a real workspace.

const folder = (c) => `intent/demo/${c}`;
const OTHER = '2026-11-02-farewell';

function finishedOf(cwd, name = CHANGE) {
  const r = run(cwd, ['state', '--json']);
  assert.equal(r.status, 0, r.stderr);
  const found = r.json.changes.find((c) => c.change === name);
  assert.ok(found, `no change ${name} in ${JSON.stringify(r.json)}`);
  assert.ok('finished' in found);
  return found.finished;
}

/** A copy of the master for a change, made as the change tool makes it. */
function copy(w, name) {
  const dir = join(w.ws, '.hoshin', 'copies', name);
  git(w.master, 'worktree', 'add', '-q', '-b', `change/${name}`, dir);
  return dir;
}

test('rejected in HEAD is finished: rejected, even when it is on the base branch', () => {
  const w = workspace();
  commit(w.master, { [`${folder(CHANGE)}/intent.md`]: intent('rejected') });
  assert.equal(finishedOf(w.master), 'rejected');
});

test('rejected in HEAD of a copy is finished: rejected', () => {
  const w = workspace();
  const dir = copy(w, OTHER);
  commit(dir, { [`${folder(OTHER)}/intent.md`]: intent('rejected') });
  assert.equal(finishedOf(w.master, OTHER), 'rejected');
});

test('rejected only in the working tree is open', () => {
  const w = workspace();
  git(w.master, 'checkout', '-q', '-b', 'topic');
  commit(w.master, { [`${folder(OTHER)}/intent.md`]: intent('accepted') });
  write(join(w.master, folder(OTHER), 'intent.md'), intent('rejected'));
  assert.equal(finishedOf(w.master, OTHER), null);
});

test('copy removed and on the local base branch is merged', () => {
  const w = workspace();
  const dir = copy(w, OTHER);
  commit(dir, { [`${folder(OTHER)}/intent.md`]: intent('accepted') });
  git(w.master, 'merge', '-q', '--ff-only', `change/${OTHER}`);
  git(w.master, 'worktree', 'remove', '--force', dir);
  assert.equal(finishedOf(w.master, OTHER), 'merged');
});

test('on the remote base branch only is merged', () => {
  const w = workspace();
  git(w.master, 'checkout', '-q', '-b', 'topic');
  commit(w.master, { [`${folder(OTHER)}/intent.md`]: intent('accepted') });
  git(w.master, 'push', '-q', 'origin', 'topic:main');
  git(w.master, 'fetch', '-q', 'origin');
  // The local main is behind origin/main; HEAD (topic) holds the merge.
  assert.equal(git(w.master, 'rev-parse', 'main') === git(w.master, 'rev-parse', 'origin/main'), false);
  assert.equal(finishedOf(w.master, OTHER), 'merged');
});

test('never had a copy and on main is merged', () => {
  const w = workspace();
  assert.equal(finishedOf(w.master), 'merged');
});

test('never had a copy and only on a feature branch is open', () => {
  const w = workspace();
  git(w.master, 'checkout', '-q', '-b', 'topic');
  commit(w.master, { [`${folder(OTHER)}/intent.md`]: intent('accepted') });
  assert.equal(finishedOf(w.master, OTHER), null);
});

test('merged but copy kept is open', () => {
  const w = workspace();
  const dir = copy(w, OTHER);
  commit(dir, { [`${folder(OTHER)}/intent.md`]: intent('accepted') });
  git(w.master, 'merge', '-q', '--ff-only', `change/${OTHER}`);
  assert.equal(finishedOf(w.master, OTHER), null);
});

test('a stray copy folder does not keep a change open', () => {
  const w = workspace();
  const stray = join(w.ws, '.hoshin', 'copies', CHANGE);
  mkdirSync(stray, { recursive: true });
  write(join(stray, 'note.txt'), 'not a worktree\n');
  assert.equal(finishedOf(w.master), 'merged');
});

test('no base branch: only rejected is finished', () => {
  const w = workspace();
  git(w.master, 'remote', 'remove', 'origin');
  git(w.master, 'branch', '-m', 'trunk');
  assert.equal(finishedOf(w.master), null);
  commit(w.master, { [`${folder(CHANGE)}/intent.md`]: intent('rejected') });
  assert.equal(finishedOf(w.master), 'rejected');
});

test('list and state agree on finished for every change of a workspace', () => {
  const cases = [];
  { const w = workspace(); commit(w.master, { [`${folder(CHANGE)}/intent.md`]: intent('rejected') }); cases.push(w); }
  { const w = workspace(); cases.push(w); }
  {
    const w = workspace();
    const dir = copy(w, OTHER);
    commit(dir, { [`${folder(OTHER)}/intent.md`]: intent('rejected') });
    cases.push(w);
  }
  {
    const w = workspace();
    const dir = copy(w, OTHER);
    commit(dir, { [`${folder(OTHER)}/intent.md`]: intent('accepted') });
    git(w.master, 'merge', '-q', '--ff-only', `change/${OTHER}`);
    cases.push(w);
  }
  {
    const w = workspace();
    git(w.master, 'checkout', '-q', '-b', 'topic');
    commit(w.master, { [`${folder(OTHER)}/intent.md`]: intent('accepted') });
    cases.push(w);
  }
  {
    const w = workspace();
    const dir = copy(w, OTHER);
    commit(dir, { [`${folder(OTHER)}/intent.md`]: intent('accepted') });
    git(w.master, 'merge', '-q', '--ff-only', `change/${OTHER}`);
    git(w.master, 'worktree', 'remove', '--force', dir);
    cases.push(w);
  }
  {
    const w = workspace();
    git(w.master, 'remote', 'remove', 'origin');
    git(w.master, 'branch', '-m', 'trunk');
    cases.push(w);
  }
  for (const w of cases) {
    const st = run(w.master, ['state', '--json']);
    const ls = run(w.master, ['list', '--json']);
    assert.equal(st.status, 0, st.stderr);
    assert.equal(ls.status, 0, ls.stderr);
    assert.ok(st.json.changes.length > 0);
    assert.deepEqual(ls.json.changes.map((c) => [c.change, c.finished]), st.json.changes.map((c) => [c.change, c.finished]));
  }
});
