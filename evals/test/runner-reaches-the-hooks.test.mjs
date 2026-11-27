// Self-test for runner-reaches-the-hooks, the runner's smoke case (B02).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, matches, skill, target, traceLine } from './lib.mjs';

const CASE = 'runner-reaches-the-hooks';

test(`${CASE}: skill-fired sees intent and not another skill`, () => {
  assert.ok(matches(CASE, 'skill-fired', skill('hoshin:intent')));
  assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:spec')));
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const ws = fixture(CASE);
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
});
