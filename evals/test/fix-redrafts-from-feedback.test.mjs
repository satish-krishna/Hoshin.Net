// Self-test for fix-redrafts-from-feedback: each grader is tried on what a good redraft leaves (one new draft fix commit
// through the program, the scenario in the spec and the plan) and on what a run that did nothing, redrafted twice,
// accepted, or touched the product repository leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, target } from './lib.mjs';
import { commonTests, edit, folderOf, withFix } from './fix-lib.mjs';

const CASE = 'fix-redrafts-from-feedback';
const CHANGE = '2026-10-24-greeting-space';
const folder = folderOf(CHANGE);
const text = (ws, grader) => target(ws, CASE, grader);

commonTests(CASE, { fires: ['fix'] });

test(`${CASE}: a redraft that carries the scenario passes every grader, and the drafted fix as it stands fails the redraft ones`, () => {
  const before = fixture(CASE);
  for (const g of ['redraft-committed-once', 'spec-has-the-new-scenario', 'spec-keeps-its-first-scenario-and-gains-one', 'plan-covers-the-new-scenario']) {
    assert.ok(!matches(CASE, g, text(before, g)), `${g} must fail before any redraft`);
  }
  for (const g of ['accepted-nothing', 'spec-still-a-draft', 'plan-still-a-draft', 'product-repository-unchanged']) {
    const hit = matches(CASE, g, text(before, g));
    assert.equal(hit, g !== 'accepted-nothing', g);
  }

  const ws = fixture(CASE);
  withFix(ws, 'redraft_with_hyphen');
  for (const g of ['redraft-committed-once', 'spec-has-the-new-scenario', 'spec-keeps-its-first-scenario-and-gains-one', 'plan-covers-the-new-scenario', 'spec-still-a-draft', 'plan-still-a-draft', 'product-repository-unchanged']) {
    assert.ok(matches(CASE, g, text(ws, g)), `${g} must pass on a good redraft`);
  }
  assert.ok(!matches(CASE, 'accepted-nothing', text(ws, 'accepted-nothing')));
});

test(`${CASE}: redraft-committed-once fails on a second new draft commit`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'redraft_with_hyphen; printf "\nNote.\n" >> "$(fix_folder)/spec.md"; commit_doc "$fix_change" fix-draft "intent/demo/$fix_change"');
  assert.ok(!matches(CASE, 'redraft-committed-once', text(ws, 'redraft-committed-once')));
});

test(`${CASE}: accepted-nothing fires, and the draft status graders fail, once the model accepts the redraft`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'redraft_with_hyphen; accept_fix');
  assert.ok(matches(CASE, 'accepted-nothing', text(ws, 'accepted-nothing')));
  assert.ok(!matches(CASE, 'spec-still-a-draft', text(ws, 'spec-still-a-draft')));
  assert.ok(!matches(CASE, 'plan-still-a-draft', text(ws, 'plan-still-a-draft')));
});

test(`${CASE}: the scenario graders fail on a redraft that replaced the first scenario or left the plan behind`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'redraft_with_hyphen');
  edit(ws, `${folder}/plan.md`, (t) => t.replaceAll('Mary-Jane', 'a visitor').replaceAll('R1.2', 'R1.1'));
  assert.ok(!matches(CASE, 'plan-covers-the-new-scenario', text(ws, 'plan-covers-the-new-scenario')), 'a plan that never took the scenario');
  edit(ws, `${folder}/spec.md`, (t) => t.replace(/ {4}@R1\.1\n {4}Scenario: A returning visitor with a first name\n[^\n]*\n[^\n]*\n[^\n]*\n\n/, ''));
  assert.ok(!matches(CASE, 'spec-keeps-its-first-scenario-and-gains-one', text(ws, 'spec-keeps-its-first-scenario-and-gains-one')), 'one scenario left');
  assert.ok(matches(CASE, 'spec-keeps-its-first-scenario-and-gains-one', 'Rule: x\n    Scenario Outline: names\n      Examples:\n    Scenario: other'), 'an Outline counts as a scenario');
});

test(`${CASE}: the product repository grader fails once the app gains a commit`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'cd app && sed -i.bak \'s/Welcome back${2:+,$2}/Welcome back${2:+, $2}/\' greet.sh && rm greet.sh.bak && git_in commit -q -a -m "fix: space"');
  assert.ok(!matches(CASE, 'product-repository-unchanged', text(ws, 'product-repository-unchanged')));
});
