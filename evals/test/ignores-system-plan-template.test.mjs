// Self-test for ignores-system-plan-template: each grader is tried on a plan in the plugin's layout and on one that follows
// the system's template. The fixture's template sits on main before the change starts, so main must gain nothing.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, read, target } from './lib.mjs';
import { commonTests } from './plan-common.mjs';
import { changeReflog, mainReflog, planText, runPlanTurn, strayCommit, writePlan } from './plan-lib.mjs';

const CASE = 'ignores-system-plan-template';
const seen = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));
const houseStyle = '---\nsource_spec: 2026-11-01-greeting/spec.md\nsystem: demo\nstatus: draft\n---\n\n# SYSTEM-TEMPLATE-MARKER: Greet\n\n## House sections\n\n- Put every task in a table.\n';

commonTests(CASE);

test(`${CASE}: the graders pass a plan in the plugin's layout, and fail one written to the system's template`, () => {
  const ws = fixture(CASE);
  assert.ok(seen(ws, 'system-template-left-as-it-was'), 'the fixture\'s copy holds the system template');
  assert.ok(matches(CASE, 'master-main-gains-no-commit', mainReflog(ws)), 'main\'s last commit is the template');
  assert.match(runPlanTurn(ws, planText()), /plan draft is committed/);
  assert.ok(seen(ws, 'plan-is-layout-2'));
  assert.ok(!seen(ws, 'system-template-not-used'), 'not_contains: no marker in the plan');
  assert.ok(matches(CASE, 'plan-commit-landed', changeReflog(ws)));
  assert.ok(matches(CASE, 'master-main-gains-no-commit', mainReflog(ws)), 'the plan commit lands in the change\'s copy, not on main');

  writePlan(ws, houseStyle);
  assert.ok(!seen(ws, 'plan-is-layout-2'), 'the system template\'s front matter is not layout 2');
  assert.ok(seen(ws, 'system-template-not-used'), 'not_contains: the marker is in the plan, which fails the run');
  writePlan(ws, planText().replace('# Plan: Greet returning visitors', '# SYSTEM-TEMPLATE-MARKER: Greet'));
  assert.ok(seen(ws, 'plan-is-layout-2') && seen(ws, 'system-template-not-used'), 'layout 2 with the template\'s heading still fails on the marker');
});

test(`${CASE}: a template that was edited or a commit on main fails`, () => {
  const ws = fixture(CASE);
  strayCommit(ws, 'master', 'docs(demo): draft plan 2026-11-01-greeting');
  assert.ok(!matches(CASE, 'master-main-gains-no-commit', mainReflog(ws)), 'a commit on main is seen');
  assert.ok(!matches(CASE, 'system-template-left-as-it-was', read(ws, 'master/intent/demo/templates/plan.md').replace('SYSTEM-TEMPLATE-MARKER', 'x')), 'an overwritten template');
});

test(`${CASE}: the reply must name the unused template, with either slash`, () => {
  assert.ok(matches(CASE, 'output-names-the-unused-template', 'The plan is ready. The system\'s intent/demo/templates/plan.md was not used: plan layout is fixed.'));
  assert.ok(matches(CASE, 'output-names-the-unused-template', 'Unused: intent\\demo\\templates\\plan.md.'));
  assert.ok(!matches(CASE, 'output-names-the-unused-template', 'The plan is written.'));
  assert.ok(!matches(CASE, 'output-names-the-unused-template', 'See the plan template.'));
});
