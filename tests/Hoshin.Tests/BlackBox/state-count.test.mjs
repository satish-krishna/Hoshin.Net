import { test } from 'node:test';
import assert from 'node:assert/strict';
import { systemWorkspace } from './helpers/system-fixture.mjs';
import { runCounted } from './helpers/workspace.mjs';

// `state` reads git once per repository, not once per change: the number of git processes it starts does not grow with
// the number of finished changes in the system.

for (const args of [[], ['--open'], ['--change', 'KIND:built']]) {
  test(`state ${args.join(' ') || '(whole system)'} starts the same number of git processes at 10 and 50 finished changes (and 2 and 10 clean open ones)`, () => {
    const small = systemWorkspace({ finished: 10, cleanOpen: 2 });
    const large = systemWorkspace({ finished: 50, cleanOpen: 10 });
    const resolveArgs = (f) => args.map((a) => (a.startsWith('KIND:') ? f.names[a.slice(5)] : a));
    const a = runCounted(small.master, ['state', '--json', ...resolveArgs(small)]);
    const b = runCounted(large.master, ['state', '--json', ...resolveArgs(large)]);
    assert.equal(a.status, 0, a.stderr);
    assert.equal(b.status, 0, b.stderr);
    assert.equal(b.gitCount, a.gitCount);
  });
}
