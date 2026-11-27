// Self-test for plan-cites-outline-examples: each grader is tried on a plan that cites the spec's Examples and on plans that
// copy them or never mention them.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, target } from './lib.mjs';
import { commonTests } from './plan-common.mjs';
import { changeReflog, planText, runPlanTurn, writePlan } from './plan-lib.mjs';

const CASE = 'plan-cites-outline-examples';
const cites = 'The decision table is the Examples of R1.1, the spec\'s Scenario Outline; this task adds no rows.';
const copied = '| visited | name | greetings | shown |\n|---|---|---|---|\n| has | Mina | on | the greeting "Welcome back, Mina" |';
const refinement = '| firstName | result |\n|---|---|\n| absent | `"Welcome back"` |';
const seen = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));
const outline = (options) => planText({ spec: 'outline', ...options });

commonTests(CASE);

test(`${CASE}: the graders pass a plan that cites the Examples and fail one that copies the rows or never cites them`, () => {
  const ws = fixture(CASE);
  assert.match(runPlanTurn(ws, outline({ change: `homeHeader decides what the home page shows. ${cites}` })), /plan draft is committed/);
  assert.ok(!seen(ws, 'no-copy-of-the-examples'), 'a citing plan is taken for a copy');
  assert.ok(seen(ws, 'change-cites-the-examples'));
  assert.ok(matches(CASE, 'plan-commit-landed', changeReflog(ws)));

  // no-copy-of-the-examples is not_contains: a match is the failure.
  writePlan(ws, outline({ change: `${cites}\n\nThe header follows this table:\n\n${copied}` }));
  assert.ok(seen(ws, 'no-copy-of-the-examples'), 'a copied table');

  writePlan(ws, outline({ change: 'homeHeader decides what the home page shows.', tests: 'A test per Examples row of R1.1 in src/home.test.ts.' }));
  assert.ok(!seen(ws, 'change-cites-the-examples'), 'Examples named only under Tests');

  // A refinement table for an input the spec does not name repeats outcomes, but not the spec's rows.
  writePlan(ws, outline({ change: `${cites}\n\n${refinement}` }));
  assert.ok(!seen(ws, 'no-copy-of-the-examples'), 'a refinement table is not a copy');
  assert.ok(seen(ws, 'change-cites-the-examples'));
});
