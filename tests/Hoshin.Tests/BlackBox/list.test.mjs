import { test } from 'node:test';
import assert from 'node:assert/strict';
import { run, runCounted, workspace } from './helpers/workspace.mjs';
import { systemWorkspace } from './helpers/system-fixture.mjs';

// `list --json`: each change's name, title, track, master, inCopy and finished, reading only the intents.

function state(cwd, ...args) {
  const r = run(cwd, ['state', '--json', ...args]);
  assert.equal(r.status, 0, r.stderr);
  return r.json;
}

test('list gives each change its name, title, track, master, inCopy and finished, sorted, and nothing else', () => {
  const f = systemWorkspace({ finished: 4 });
  const r = run(f.master, ['list', '--json']);
  assert.equal(r.status, 0, r.stderr);
  const st = state(f.master);
  assert.ok(st.changes.length > 4);
  assert.deepEqual(r.json, {
    system: 'demo',
    changes: st.changes.map(({ change, title, track, master, inCopy, finished }) => ({ change, title, track, master, inCopy, finished })),
    problems: [],
  });
  for (const c of r.json.changes) assert.deepEqual(Object.keys(c), ['change', 'title', 'track', 'master', 'inCopy', 'finished']);
});

test('list starts the same number of git processes at 10 and 50 finished changes (and 2 and 10 clean open ones), and fewer than state', () => {
  const small = systemWorkspace({ finished: 10, cleanOpen: 2 });
  const large = systemWorkspace({ finished: 50, cleanOpen: 10 });
  const a = runCounted(small.master, ['list', '--json']);
  const b = runCounted(large.master, ['list', '--json']);
  assert.equal(a.status, 0, a.stderr);
  assert.equal(b.status, 0, b.stderr);
  assert.equal(b.gitCount, a.gitCount);
  const s = runCounted(large.master, ['state', '--json']);
  assert.ok(b.gitCount < s.gitCount, `list ${b.gitCount} vs state ${s.gitCount}`);
});

test('list: unknown system, several systems without --system, wrong arguments exit 2', () => {
  const w = workspace();
  assert.equal(run(w.master, ['list', '--json', '--system', 'other']).status, 2);
  assert.equal(run(w.master, ['list']).status, 2);
  assert.equal(run(w.master, ['list', '--json', 'extra']).status, 2);
  assert.equal(run(w.master, ['list', '--json', '--change', 'x']).status, 2);
  const two = workspace({ marker: { systems: { demo: { repos: ['app'] }, other: { repos: ['app'] } } } });
  const r = run(two.master, ['list', '--json']);
  assert.equal(r.status, 2);
  assert.match(r.stderr, /--system/);
  assert.deepEqual(run(two.master, ['list', '--json', '--system', 'other']).json.changes, []);
});
