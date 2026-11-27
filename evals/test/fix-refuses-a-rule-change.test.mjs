// Self-test for fix-refuses-a-rule-change: each grader is tried on what a passing run leaves and on what a failing
// run leaves. This file is the model for every case's self-test (evals/README.md, "Grader self-tests").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'fix-refuses-a-rule-change';

test(`${CASE}: the skill grader sees fix or start fired, plain or namespaced, and nothing else`, () => {
  assert.ok(matches(CASE, 'skill-fired', skill('hoshin:fix')));
  assert.ok(matches(CASE, 'skill-fired', skill('start')));
  assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:intent')));
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const ws = fixture(CASE);
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')), 'a fixture with no run must have no trace');
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
});

test(`${CASE}: the refusal graders see the rule and the intent route, and not a refusal that names neither`, () => {
  const good = 'This is not a small change: it changes context rule C2 (a first-time visitor sees no greeting), which an engineer confirmed. Run /hoshin:intent to take it through the pipeline.';
  assert.ok(matches(CASE, 'output-names-the-rule', good));
  assert.ok(matches(CASE, 'output-points-to-intent', good));
  assert.ok(matches(CASE, 'output-names-the-rule', 'That would change the rule that a first-time visitor sees no greeting.'));
  const vague = 'This change is too big for the light track. Use the full pipeline instead.';
  assert.ok(!matches(CASE, 'output-names-the-rule', vague));
  assert.ok(!matches(CASE, 'output-points-to-intent', vague));
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -m "fix: greet"')));
  assert.ok(matches(CASE, 'no-hand-commit', bash('cd app && git commit -am wip')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git status')));
});

test(`${CASE}: the master's reflog passes as built, and fails once anything is committed on main`, () => {
  const ws = fixture(CASE);
  assert.ok(matches(CASE, 'master-main-gains-no-commit', target(ws, CASE, 'master-main-gains-no-commit')));
  withHelpers(ws, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft intent x"');
  assert.ok(!matches(CASE, 'master-main-gains-no-commit', target(ws, CASE, 'master-main-gains-no-commit')));
});

test(`${CASE}: a change started through the program and left behind is what no-change-left-behind looks for`, () => {
  // file_exists is judged by the runner; this pins that a started change leaves the loose ref the grader's path names.
  const ws = fixture(CASE);
  withHelpers(ws, 'change_start 2026-11-01-greet-new-visitors feat greeting');
  assert.equal(withHelpers(ws, 'ls master/.git/refs/heads/change/').trim(), '2026-11-01-greet-new-visitors');
});
