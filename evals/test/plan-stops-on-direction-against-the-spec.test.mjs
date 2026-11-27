// Self-test for plan-stops-on-direction-against-the-spec: a run that declines leaves no plan and no commit; a run that writes
// a plan anyway leaves both. file_exists graders cannot be evaluated in node, so the test pins what their paths depend on.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, matches } from './lib.mjs';
import { commonTests } from './plan-common.mjs';
import { changeReflog, planPath, planRel, planText, runPlanTurn } from './plan-lib.mjs';

const CASE = 'plan-stops-on-direction-against-the-spec';

commonTests(CASE);

test(`${CASE}: a model that declines to write leaves the change as it was, and the Stop hook lets the turn end`, () => {
  const ws = fixture(CASE);
  assert.ok(matches(CASE, 'change-gained-no-commit', changeReflog(ws)), 'the fixture\'s last commit is the spec acceptance');
  assert.equal(runPlanTurn(ws, null), '', 'a declined plan is a clean outcome, not a block');
  assert.ok(matches(CASE, 'change-gained-no-commit', changeReflog(ws)));
  assert.ok(!existsSync(planPath(ws)), 'no plan.md in the copy');
});

test(`${CASE}: a model that writes the plan anyway leaves a plan, a commit, and a file at the paths the file_exists graders look at`, () => {
  const ws = fixture(CASE);
  assert.match(planRel(), /^\.hoshin\/copies\/2026-11-01-greeting\/intent\/demo\/2026-11-01-greeting\/plan\.md$/);
  runPlanTurn(ws, planText({ direction: '- A returning visitor always sees Hello again → T1.' }));
  assert.ok(existsSync(join(ws, planRel())), 'no-plan-written: the copy\'s path is where a plan lands');
  assert.ok(!existsSync(join(ws, 'master', 'intent', 'demo', '2026-11-01-greeting')), 'no-plan-written-in-the-master: main has no change folder, so only the copy\'s path can see a plan');
  assert.ok(!matches(CASE, 'change-gained-no-commit', changeReflog(ws)), 'a plan commit is seen');
});

test(`${CASE}: the reply must name the contradiction and the spec reopen`, () => {
  const good = 'This direction contradicts R1.1, where a returning visitor who gave the name Mina sees it. Reopen the spec, then plan again.';
  assert.ok(matches(CASE, 'names-the-contradiction', good));
  assert.ok(matches(CASE, 'names-the-spec-reopen', good));
  assert.ok(matches(CASE, 'names-the-spec-reopen', 'The spec must be reopened first.'));
  assert.ok(matches(CASE, 'names-the-contradiction', 'The direction drops the first name that the spec greets.'));
  const wrote = 'The plan is written and committed.';
  assert.ok(!matches(CASE, 'names-the-spec-reopen', wrote));
  assert.ok(!matches(CASE, 'names-the-contradiction', wrote));
  assert.ok(!matches(CASE, 'names-the-spec-reopen', 'I cannot plan this: the direction conflicts with R1.1.'), 'a refusal that never says to reopen the spec');
});
