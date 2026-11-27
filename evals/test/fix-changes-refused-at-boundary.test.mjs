// Self-test for fix-changes-refused-at-boundary: each grader is tried on what a refusal leaves (the fixture as built,
// after the first round of feedback) and on what a run that redrafted again, or lost the earlier redraft, leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, target } from './lib.mjs';
import { boundaryReplies, commonTests, edit, folderOf, withFix } from './fix-lib.mjs';

const CASE = 'fix-changes-refused-at-boundary';
const CHANGE = '2026-10-24-greeting-space';
const folder = folderOf(CHANGE);
const text = (ws, grader) => target(ws, CASE, grader);
const KEPT = ['spec-keeps-the-redraft-and-not-the-change', 'plan-left-without-the-change', 'intent-left-without-the-change'];

commonTests(CASE, { fires: ['fix'] });

test(`${CASE}: a refusal leaves every file and reflog grader passing, as the fixture stands`, () => {
  const ws = fixture(CASE);
  for (const g of ['change-gained-only-the-redraft', 'master-main-gains-no-commit', ...KEPT]) assert.ok(matches(CASE, g, text(ws, g)), g);
});

test(`${CASE}: change-gained-only-the-redraft fails when the run commits again or the redraft is missing`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'accept_fix');
  assert.ok(!matches(CASE, 'change-gained-only-the-redraft', text(ws, 'change-gained-only-the-redraft')), 'acceptances after the redraft');
  const again = fixture(CASE);
  withFix(again, 'printf "\nNote.\n" >> "$(fix_folder)/spec.md"; commit_doc "$fix_change" fix-draft "intent/demo/$fix_change"');
  assert.ok(!matches(CASE, 'change-gained-only-the-redraft', text(again, 'change-gained-only-the-redraft')), 'a third draft fix commit');
  // The first-round fixture of the sibling case has one draft only: the redraft the case depends on is missing.
  const sibling = fixture('fix-feedback-refused-at-boundary');
  assert.ok(!matches(CASE, 'change-gained-only-the-redraft', target(sibling, CASE, 'change-gained-only-the-redraft')));
});

test(`${CASE}: master-main-gains-no-commit fails once anything is committed on main`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft intent x"');
  assert.ok(!matches(CASE, 'master-main-gains-no-commit', text(ws, 'master-main-gains-no-commit')));
});

test(`${CASE}: the document graders keep the redraft and fail on the web change, on bold, on an acceptance, and on a lost redraft`, () => {
  const ws = fixture(CASE);
  for (const [doc, grader] of [['intent.md', KEPT[2]], ['spec.md', KEPT[0]], ['plan.md', KEPT[1]]]) {
    const original = text(ws, grader);
    assert.ok(matches(CASE, grader, original), `${doc} as the redraft left it`);
    assert.ok(!matches(CASE, grader, `${original}\nThe landing page in the web repository shows the greeting.\n`), `${doc} with web in it`);
    assert.ok(!matches(CASE, grader, `${original}\nThe greeting is shown in bold.\n`), `${doc} with bold in it`);
    assert.ok(!matches(CASE, grader, original.replace('"status": "draft"', '"status": "accepted"')), `${doc} accepted`);
  }
  // The spec the redraft wrote has Mary-Jane; one the run reverted to the first draft does not.
  assert.match(text(ws, KEPT[0]), /Mary-Jane/);
  edit(ws, `${folder}/spec.md`, (t) => t.replaceAll('Mary-Jane', 'Mina'));
  assert.ok(!matches(CASE, KEPT[0], text(ws, KEPT[0])), 'the redraft lost');
});

test(`${CASE}: the refusal graders see the one-repository rule and the intent route, and not a refusal that names neither`, () => {
  assert.ok(matches(CASE, 'output-names-the-one-repository-rule', boundaryReplies.good));
  assert.ok(matches(CASE, 'output-points-to-intent', boundaryReplies.good));
  assert.ok(matches(CASE, 'output-points-to-intent', boundaryReplies.intentSkill));
  assert.ok(!matches(CASE, 'output-names-the-one-repository-rule', boundaryReplies.noRule));
  assert.ok(!matches(CASE, 'output-points-to-intent', boundaryReplies.noIntent));
});
