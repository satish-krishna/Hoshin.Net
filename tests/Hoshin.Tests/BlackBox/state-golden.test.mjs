import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, realpathSync } from 'node:fs';
import { join } from 'node:path';
import { systemWorkspace } from './helpers/system-fixture.mjs';
import { git, run, runCounted, testsRoot } from './helpers/workspace.mjs';

const fixtures = join(testsRoot, 'Fixtures', 'state-golden');

function golden(name) {
  return JSON.parse(readFileSync(join(fixtures, name), 'utf8'));
}

/** Normalizes paths (`<ws>`), strips `finished`, and replaces 40-hex commit SHAs with `<sha>` so JSONC vs YAML fixture SHAs compare semantically 1:1. */
export function normalize(json, ws) {
  const pattern =
    ws === '<ws>'
      ? null
      : new RegExp(
          [...new Set([ws, realpathSync.native(ws)])]
            .flatMap((r) => [r, r.replaceAll('\\', '/')])
            .map((r) => r.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
            .join('|'),
          'gi',
        );
  const walk = (v) => {
    if (typeof v === 'string') {
      if (/^[0-9a-f]{40}$/.test(v)) return '<sha>';
      return pattern && v.search(pattern) >= 0 ? v.replace(pattern, '<ws>').replaceAll('\\', '/') : v;
    }
    if (Array.isArray(v)) return v.map(walk);
    if (v !== null && typeof v === 'object') return Object.fromEntries(Object.entries(v).filter(([k]) => k !== 'finished').map(([k, x]) => [k, walk(x)]));
    return v;
  };
  return walk(json);
}

test('state output matches the golden files recorded from the TypeScript reference reader', () => {
  const f = systemWorkspace({ finished: 12 });
  const whole = run(f.master, ['state', '--json']);
  assert.equal(whole.status, 0, whole.stderr);
  assert.deepEqual(normalize(whole.json, f.ws), normalize(golden('whole.json'), '<ws>'));
  const each = Object.values(f.names).sort().map((c) => {
    const r = run(f.master, ['state', '--json', '--change', c]);
    assert.equal(r.status, 0, r.stderr);
    return normalize(r.json, f.ws);
  });
  assert.deepEqual(each, normalize(golden('changes.json'), '<ws>'));
});

test('the fixture is deterministic', () => {
  const a = systemWorkspace({ finished: 3 });
  const b = systemWorkspace({ finished: 3 });
  assert.equal(git(a.master, 'rev-parse', 'HEAD'), git(b.master, 'rev-parse', 'HEAD'));
  assert.equal(git(a.app, 'rev-parse', 'HEAD'), git(b.app, 'rev-parse', 'HEAD'));
});

test('the counter counts the git processes of one run and leaves its output alone', () => {
  const f = systemWorkspace({ finished: 1 });
  const counted = runCounted(f.master, ['state', '--json']);
  assert.equal(counted.status, 0, counted.stderr);
  assert.ok(counted.gitCount > 10, `counted ${counted.gitCount}`);
  assert.deepEqual(counted.json, run(f.master, ['state', '--json']).json);
});

test('every change of the fixture says whether it is finished', () => {
  const f = systemWorkspace({ finished: 12 });
  const whole = run(f.master, ['state', '--json']);
  assert.equal(whole.status, 0, whole.stderr);
  const n = f.names;
  const expected = {
    [n.fixFinished]: 'merged',
    [n.rejectedOnMain]: 'rejected',
    [n.rejectedOnWork]: 'rejected',
    [n.mergedCopyKept]: null,
    ...Object.fromEntries(['draft', 'specDraft', 'planned', 'built', 'fixBuilt', 'fixDirty', 'fullDirty', 'inCopy'].map((k) => [n[k], null])),
  };
  for (const c of whole.json.changes) {
    assert.ok('finished' in c, c.change);
    assert.equal(c.finished, c.change in expected ? expected[c.change] : 'merged', c.change);
  }
});
