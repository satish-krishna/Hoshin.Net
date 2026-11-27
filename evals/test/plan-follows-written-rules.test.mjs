// Self-test for plan-follows-written-rules: each grader is tried on a plan that carries the two rules the code does not show
// and on plans that miss them or restate the style rule.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, target } from './lib.mjs';
import { commonTests } from './plan-common.mjs';
import { changeReflog, planText, runPlanTurn, writePlan } from './plan-lib.mjs';

const CASE = 'plan-follows-written-rules';
const rules = ['src/home.ts', 'src/copy/en.json', 'CHANGELOG.md', 'src/home.test.ts'];
const seen = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));

commonTests(CASE);

test(`${CASE}: the graders pass a plan that lists the copy file and the changelog, and fail one that reads only the code or restates the style rule`, () => {
  const ws = fixture(CASE);
  assert.match(runPlanTurn(ws, planText({ files: rules })), /plan draft is committed/);
  for (const grader of ['a-task-changes-the-copy-file', 'a-task-adds-the-changelog-line']) assert.ok(seen(ws, grader), grader);
  assert.ok(!seen(ws, 'style-rule-left-to-the-build'));
  assert.ok(matches(CASE, 'plan-commit-landed', changeReflog(ws)));

  writePlan(ws, planText({ change: 'The header greets. CHANGELOG.md and src/copy/en.json are not changed.' }));
  for (const grader of ['a-task-changes-the-copy-file', 'a-task-adds-the-changelog-line']) assert.ok(!seen(ws, grader), `${grader}: prose is not files`);

  // style-rule-left-to-the-build is not_contains: a match is the failure.
  writePlan(ws, planText({ files: rules, change: 'Give `homeHeader` a doc comment. The header greets.' }));
  assert.ok(seen(ws, 'style-rule-left-to-the-build'));
  assert.ok(matches(CASE, 'style-rule-left-to-the-build', 'Add a doc-comment to it.'));
});
