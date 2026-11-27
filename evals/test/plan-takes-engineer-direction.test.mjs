// Self-test for plan-takes-engineer-direction: each grader is tried on what a passing run leaves and on what a failing run
// leaves. A passing run ends with the Stop hook committing a plan that lists the module and records the direction. One
// fixture serves every plan: only the first goes through the hooks (a fixture costs about twenty program starts).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, target } from './lib.mjs';
import { commonTests } from './plan-common.mjs';
import { changeReflog, planText, runPlanTurn, writePlan } from './plan-lib.mjs';

const CASE = 'plan-takes-engineer-direction';
const landed = '- Greeting texts live in `src/greetings.ts`, and `homeHeader` imports them → T1.';
const withModule = ['src/greetings.ts', 'src/home.ts', 'src/home.test.ts'];
const seen = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));

commonTests(CASE);

test(`${CASE}: the graders see a plan that lists the module and records the direction, committed by the Stop hook`, () => {
  const ws = fixture(CASE);
  assert.ok(!matches(CASE, 'plan-commit-landed', changeReflog(ws)), 'no plan commit before the run');
  assert.match(runPlanTurn(ws, planText({ files: withModule, direction: landed })), /plan draft is committed/);
  for (const grader of ['a-task-changes-greetings', 'direction-recorded']) assert.ok(seen(ws, grader), grader);
  assert.ok(matches(CASE, 'plan-commit-landed', changeReflog(ws)), 'the program committed the plan');

  writePlan(ws, planText({ direction: '- None given.' }));
  for (const grader of ['a-task-changes-greetings', 'direction-recorded']) assert.ok(!seen(ws, grader), `ignored direction: ${grader}`);

  writePlan(ws, planText({ change: 'The header greets, using src/greetings.ts.', direction: landed }));
  assert.ok(!seen(ws, 'a-task-changes-greetings'), 'named in prose, not in files');
  assert.ok(seen(ws, 'direction-recorded'));

  writePlan(ws, planText({ files: withModule, direction: '- Use a feature flag.', risks: '- src/greetings.ts is new.' }));
  assert.ok(seen(ws, 'a-task-changes-greetings'));
  assert.ok(!seen(ws, 'direction-recorded'), 'the path after the section is not a recorded direction');

  writePlan(ws, planText({ files: withModule }));
  assert.ok(!seen(ws, 'direction-recorded'), 'no section at all');
});

test(`${CASE}: another document's commit is not the plan's`, () => {
  assert.ok(!matches(CASE, 'plan-commit-landed', 'a b eval <e> 1 +0000\tcommit: docs(demo): write spec 2026-11-01-greeting\n'));
  assert.ok(matches(CASE, 'plan-commit-landed', 'a b eval <e> 1 +0000\tcommit: docs(PROJ-1): draft plan 2026-11-01-greeting\n'));
});
