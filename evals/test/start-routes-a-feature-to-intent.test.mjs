// Self-test for start-routes-a-feature-to-intent: each grader is tried on what a passing run leaves and on what a failing run leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'start-routes-a-feature-to-intent';

test(`${CASE}: the skill graders see start, and a run of fix or intent`, () => {
  assert.ok(matches(CASE, 'start-fired', skill('hoshin:start')));
  assert.ok(!matches(CASE, 'start-fired', skill('hoshin:intent')));
  assert.ok(matches(CASE, 'fix-never-ran', skill('hoshin:fix')), 'a fix run is what it forbids');
  assert.ok(!matches(CASE, 'fix-never-ran', skill('hoshin:start')));
  assert.ok(matches(CASE, 'intent-never-ran', skill('hoshin:intent', 'returning visitors get a profile')), 'an intent interview is what it forbids');
  assert.ok(matches(CASE, 'intent-never-ran', skill('intent')));
  assert.ok(!matches(CASE, 'intent-never-ran', skill('hoshin:start')), 'start is not intent');
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const ws = fixture(CASE);
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
});

test(`${CASE}: the command grader needs the product owner's command with an idea, not a mention of the skill`, () => {
  for (const ok of [
    'A product owner should run this:\n\n`/hoshin:intent Returning visitors get a profile they can edit`',
    'The product owner can start it with:\n\n```\n/hoshin:intent Returning visitors get a profile where they can edit the name.\n```\n',
    'Run `hoshin:intent returning visitors get a profile with a saved language`.',
    '`intent Returning visitors get a profile they can edit`',
    '/intent Returning visitors get a profile they can edit',
  ]) {
    assert.ok(matches(CASE, 'output-gives-the-intent-command', ok), ok);
  }
  for (const no of ['This needs the intent skill; the intent is clear.', 'Run /hoshin:intent.', 'The product owner should take it through the intent pipeline.']) {
    assert.ok(!matches(CASE, 'output-gives-the-intent-command', no), no);
  }
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -m "docs(demo): draft intent"')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
});

test(`${CASE}: a routed feature leaves main, the change branches and the copies alone`, () => {
  const ws = fixture(CASE);
  assert.ok(matches(CASE, 'master-gained-no-commit', target(ws, CASE, 'master-gained-no-commit')));
  assert.ok(!existsSync(join(ws, 'master', '.git', 'refs', 'heads', 'change')));
  assert.ok(!existsSync(join(ws, '.hoshin', 'copies')));
  withHelpers(ws, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft intent x"');
  assert.ok(!matches(CASE, 'master-gained-no-commit', target(ws, CASE, 'master-gained-no-commit')));
  withHelpers(ws, 'change_start 2026-11-01-profile feat demo');
  assert.equal(withHelpers(ws, 'ls master/.git/refs/heads/change/').trim(), '2026-11-01-profile');
  assert.ok(existsSync(join(ws, '.hoshin', 'copies')));
});
