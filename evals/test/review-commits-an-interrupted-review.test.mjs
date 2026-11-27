// Self-test for review-commits-an-interrupted-review. The workspace starts with build/review.md written and untracked
// in the change's copy. The tests that read that state as built come first; the graders every review case shares come
// from review-helpers.mjs, with review: null so the draft is committed as it is.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { matches, target, withHelpers } from './lib.mjs';
import { finding, lazyFixture, registerCommonTests, reviewText } from './review-helpers.mjs';

const CASE = 'review-commits-an-interrupted-review';
const CHANGE = '2026-11-01-vip-greeting';
const DRAFT = 'An interrupted draft: a name with surrounding spaces is echoed as given.';
const workspace = lazyFixture(CASE);

test(`${CASE}: as built, the draft is on disk and untracked, and the graders on its text pass`, () => {
  const dir = workspace();
  assert.match(withHelpers(dir, `git -C "$(copy_of ${CHANGE})" status --short`), /\?\? intent\/demo\/2026-11-01-vip-greeting\/build\/review\.md/, 'the draft is untracked');
  assert.ok(matches(CASE, 'draft-kept', target(dir, CASE, 'draft-kept')));
  assert.ok(!matches(CASE, 'no-second-review', target(dir, CASE, 'no-second-review')), 'no Review 2 in the draft');
  assert.ok(!matches(CASE, 'no-new-finding', target(dir, CASE, 'no-new-finding')), 'no F2 in the draft');
  const state = withHelpers(dir, `cd master && hoshin state --json --system demo --change ${CHANGE}`);
  assert.doesNotMatch(state, /"review":\{"committed":0,"draft":"none"\}/, 'the program sees a draft review');
});

registerCommonTests(CASE, { change: CHANGE, review: null, repos: { 'product-repository-unchanged': 'app' }, workspace });

test(`${CASE}: committed as it is, the draft keeps its text and gains no second section or finding`, () => {
  const dir = workspace();
  assert.ok(matches(CASE, 'draft-kept', target(dir, CASE, 'draft-kept')));
  assert.ok(!matches(CASE, 'no-second-review', target(dir, CASE, 'no-second-review')));
  assert.ok(!matches(CASE, 'no-new-finding', target(dir, CASE, 'no-new-finding')));
  // The shared tests left a stray empty commit on top; the file in HEAD is still the draft the program committed.
  const committed = withHelpers(dir, `git -C "$(copy_of ${CHANGE})" show HEAD:intent/demo/${CHANGE}/build/review.md`);
  assert.ok(matches(CASE, 'draft-kept', committed), 'the committed file holds the draft');
});

test(`${CASE}: a review started afresh fails the graders, whether it replaces the draft or follows it`, () => {
  const bug = (id, file, line) => finding(id, { severity: 'low', repo: 'app', concern: 'bug', file, line }, DRAFT);
  const afresh = reviewText({ findings: [finding('F1', { severity: 'high', repo: 'app', concern: 'bug', file: 'vip.sh', line: 1 }, 'A new finding.')] });
  assert.ok(!matches(CASE, 'draft-kept', afresh), 'the draft was discarded');
  assert.ok(matches(CASE, 'no-second-review', reviewText({ sections: 2 })), 'a Review 2 is seen');
  assert.ok(matches(CASE, 'no-new-finding', reviewText({ findings: [bug('F1', 'greet.sh', 4), bug('F2', 'vip.sh', 1)] })), 'an F2 is seen');
  assert.ok(matches(CASE, 'draft-kept', reviewText({ findings: [bug('F1', 'greet.sh', 4)] })));
});

test(`${CASE}: any Agent call is a dispatched reviewer, and the grader allows none`, () => {
  const call = JSON.stringify({ description: 'Review app', subagent_type: 'general-purpose', prompt: 'Review the change in app.' });
  assert.ok(matches(CASE, 'no-reviewer-dispatched', call), 'an Agent call matches, so max 0 fails the run');
  assert.ok(!matches(CASE, 'no-reviewer-dispatched', ''), 'an empty input is not a call');
});
