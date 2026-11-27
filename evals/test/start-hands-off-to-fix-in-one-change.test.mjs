// Self-test for start-hands-off-to-fix-in-one-change: each grader is tried on what a passing run leaves and on what a failing run leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, graders, matches, read, skill, target, traceLine, withHelpers } from './lib.mjs';
import { CHANGE, copyLog, documentPath, draftFix, redraftFix, traceOf } from './start-docs.mjs';

const CASE = 'start-hands-off-to-fix-in-one-change';
const on = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));

// tool_order: some call matches `before`, and a later one matches `after`. Calls are the Skill inputs of a run, in order.
function inOrder(calls) {
  const g = graders(CASE)['start-before-fix'];
  const first = calls.findIndex((c) => new RegExp(g.before, 'm').test(c));
  return first >= 0 && calls.slice(first + 1).some((c) => new RegExp(g.after, 'm').test(c));
}

test(`${CASE}: the skill graders see start, then fix, in that order, and a refused intent`, () => {
  assert.ok(matches(CASE, 'start-fired', skill('hoshin:start')));
  assert.ok(!matches(CASE, 'start-fired', skill('hoshin:fix')));
  assert.ok(matches(CASE, 'fix-fired', skill('hoshin:fix', CHANGE)));
  assert.ok(matches(CASE, 'intent-never-ran', skill('hoshin:intent')));
  assert.ok(inOrder([skill('hoshin:start'), skill('hoshin:fix', CHANGE)]), 'start, then fix');
  assert.ok(inOrder([skill('start'), skill('fix')]), 'plain names');
  assert.ok(!inOrder([skill('hoshin:fix', CHANGE), skill('hoshin:start')]), 'fix, then start');
  assert.ok(!inOrder([skill('hoshin:fix', CHANGE)]), 'fix alone: start never ran');
  assert.ok(!inOrder([skill('hoshin:start')]), 'start alone: nothing was handed on');
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const ws = fixture(CASE);
  assert.ok(!on(ws, 'hook-trace-has-an-entry'));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(on(ws, 'hook-trace-has-an-entry'));
});

test(`${CASE}: exactly one hook step drafts a fix, and a second one means a second change`, () => {
  assert.ok(matches(CASE, 'one-hook-step-drafted-a-fix', traceOf('start: Start', 'fix: Fix', 'fix: AwaitAcceptFix')));
  assert.ok(matches(CASE, 'one-hook-step-drafted-a-fix', traceOf('fix: Fix')));
  assert.ok(!matches(CASE, 'one-hook-step-drafted-a-fix', traceOf('fix: Fix', 'fix: Fix')), 'a second change drafted again');
  assert.ok(!matches(CASE, 'one-hook-step-drafted-a-fix', traceOf('start: Start', 'fix: refused, next is AwaitAcceptFix')), 'no fix was drafted');
  assert.ok(!matches(CASE, 'one-hook-step-drafted-a-fix', traceLine()), 'a trace with no decisions');
});

test(`${CASE}: one start commit and one draft commit in the named copy pass, and a second of either fails`, () => {
  const ws = fixture(CASE);
  assert.ok(!on(ws, 'change-started-once'), 'no change in the fixture');
  assert.ok(!on(ws, 'drafted-once'));
  draftFix(ws);
  assert.ok(on(ws, 'change-started-once'));
  assert.ok(on(ws, 'drafted-once'));
  assert.ok(on(ws, 'master-main-gains-no-commit'));
  assert.equal(read(ws, documentPath('plan')) !== '', true, 'documents-written-in-the-named-change looks where the plan lands');
  assert.ok(!matches(CASE, 'accepted-nothing', read(ws, copyLog())), 'a draft is not an acceptance');
  redraftFix(ws);
  assert.ok(!on(ws, 'drafted-once'), 'two draft commits');
  // The program never starts the same change twice; a second start commit is a stray commit the engine did not make.
  withHelpers(ws, `cd "$(copy_of ${CHANGE})" && git_in commit -q --allow-empty -m "docs(demo): start change ${CHANGE}"`);
  assert.ok(!on(ws, 'change-started-once'), 'two start commits');
});

test(`${CASE}: an acceptance and a hand commit on main are what the failing run leaves`, () => {
  const ws = fixture(CASE);
  draftFix(ws);
  const doc = `.hoshin/copies/${CHANGE}/intent/demo/${CHANGE}/intent.md`;
  withHelpers(ws, `sed -i.bak 's/"status": "draft"/"status": "accepted"/' "${doc}" && rm "${doc}.bak" && commit_doc ${CHANGE} accept-intent intent/demo/${CHANGE}/intent.md`);
  assert.ok(matches(CASE, 'accepted-nothing', read(ws, copyLog())));
  withHelpers(ws, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft intent x"');
  assert.ok(!on(ws, 'master-main-gains-no-commit'));
  assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -m x')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
});
