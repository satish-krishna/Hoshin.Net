// Self-test for plan-applies-an-outline-correction: each grader is tried on what a passing run leaves and on what a failing
// run leaves. The outline itself is not graded, only what the correction left in the committed plan.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, target } from './lib.mjs';
import { commonTests } from './plan-common.mjs';
import { changeReflog, planText, runPlanTurn, writePlan } from './plan-lib.mjs';

const CASE = 'plan-applies-an-outline-correction';
const landed = '- Greeting texts live in `src/greetings.ts` (outline correction) → T1.';
const withModule = ['src/greetings.ts', 'src/home.ts', 'src/home.test.ts'];
const seen = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));

commonTests(CASE);

test(`${CASE}: the graders see a plan that applied the correction and recorded it, and fail one that dropped or half-applied it`, () => {
  const ws = fixture(CASE);
  assert.ok(!matches(CASE, 'plan-commit-landed', changeReflog(ws)));
  assert.match(runPlanTurn(ws, planText({ files: withModule, direction: landed })), /plan draft is committed/);
  for (const grader of ['a-task-changes-greetings', 'direction-recorded']) assert.ok(seen(ws, grader), grader);
  assert.ok(matches(CASE, 'plan-commit-landed', changeReflog(ws)));

  writePlan(ws, planText({ direction: '- None given.' }));
  for (const grader of ['a-task-changes-greetings', 'direction-recorded']) assert.ok(!seen(ws, grader), `dropped: ${grader}`);

  writePlan(ws, planText({ direction: landed }));
  assert.ok(seen(ws, 'direction-recorded'));
  assert.ok(!seen(ws, 'a-task-changes-greetings'), 'recorded but in no task');
});

test(`${CASE}: the acceptance of the spec is not the plan's commit`, () => {
  assert.ok(!matches(CASE, 'plan-commit-landed', 'a b eval <e> 1 +0000\tcommit: docs(demo): accept spec 2026-11-01-greeting\n'));
});
