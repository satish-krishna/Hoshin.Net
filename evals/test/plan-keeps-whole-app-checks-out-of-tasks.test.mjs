// Self-test for plan-keeps-whole-app-checks-out-of-tasks: each grader is tried on a scoped plan that names the whole-app
// checks under Checks at build finish and on plans that put them in a task or leave them out.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, target } from './lib.mjs';
import { commonTests } from './plan-common.mjs';
import { changeReflog, planText, runPlanTurn, writePlan } from './plan-lib.mjs';

const CASE = 'plan-keeps-whole-app-checks-out-of-tasks';
const checks = '- `app`: `npm run build` and `npm run check:boot`, because the greeting changes the home route.';
const message = 'Add `npm run check:boot` and `npm run build` as build.finish hooks on the master repository\'s base branch before the build starts.';
const seen = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));

commonTests(CASE);

test(`${CASE}: a scoped plan that names the checks passes every grader, committed by the Stop hook`, () => {
  const ws = fixture(CASE);
  assert.match(runPlanTurn(ws, planText({ checks })), /plan draft is committed/);
  assert.ok(seen(ws, 'plan-is-layout-2'));
  assert.ok(!seen(ws, 'no-done-when-runs-a-whole-app-check'), 'a scoped plan is taken for a whole-app check');
  assert.ok(!seen(ws, 'no-task-command-runs-a-whole-app-check'));
  for (const grader of ['a-task-runs-a-filtered-test', 'checks-section-names-the-browser-check', 'checks-section-names-the-build']) assert.ok(seen(ws, grader), grader);
  assert.ok(matches(CASE, 'plan-commit-landed', changeReflog(ws)));

  // The two no-* graders are not_contains: a match is the failure.
  for (const doneWhen of ['`npm run check:boot` passes.', '`npm run build` passes.', '`npm test` passes.', '`npm test -- src/home.test.ts` and `npm run check:boot` pass.']) {
    writePlan(ws, planText({ checks, doneWhen }));
    assert.ok(seen(ws, 'no-done-when-runs-a-whole-app-check'), `not seen: ${doneWhen}`);
  }
  writePlan(ws, planText({ checks, test: ['node', 'scripts/check-boot.mjs'] }));
  assert.ok(seen(ws, 'no-task-command-runs-a-whole-app-check'));
  writePlan(ws, planText({ checks, test: ['node', 'node_modules/vite/bin/vite.js', 'build'] }));
  assert.ok(seen(ws, 'no-task-command-runs-a-whole-app-check'));
  writePlan(ws, planText({ checks, test: ['node', 'node_modules/vitest/vitest.mjs', 'run'] }));
  assert.ok(!seen(ws, 'a-task-runs-a-filtered-test'), 'an unfiltered run is not a filtered one');
});

test(`${CASE}: a plan with no Checks section, one naming only the build, or one naming a check outside the section fails the section graders`, () => {
  const ws = fixture(CASE);
  writePlan(ws, planText({}));
  assert.ok(!seen(ws, 'checks-section-names-the-browser-check'));
  assert.ok(!seen(ws, 'checks-section-names-the-build'));
  writePlan(ws, planText({ checks: '- `app`: `npm run build`.' }));
  assert.ok(!seen(ws, 'checks-section-names-the-browser-check'), 'the build alone named');
  assert.ok(seen(ws, 'checks-section-names-the-build'));
  writePlan(ws, planText({ checks: '- nothing beyond the hooks.', risks: '- `npm run check:boot` could fail at build finish.' }));
  assert.ok(!seen(ws, 'checks-section-names-the-browser-check'));
  assert.ok(!seen(ws, 'no-done-when-runs-a-whole-app-check'), 'a whole-app check outside every done-when is not a task\'s');
});

test(`${CASE}: the closing message must tell the engineer to add the hooks on the base branch`, () => {
  assert.ok(matches(CASE, 'output-says-add-the-hooks-on-the-base-branch', message));
  assert.ok(!matches(CASE, 'output-says-add-the-hooks-on-the-base-branch', 'The plan is ready for review.'));
  assert.ok(!matches(CASE, 'output-says-add-the-hooks-on-the-base-branch', 'The base branch is main.'));
});
