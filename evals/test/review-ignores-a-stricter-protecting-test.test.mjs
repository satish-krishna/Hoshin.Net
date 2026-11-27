// Self-test for review-ignores-a-stricter-protecting-test. The graders every review case shares come from
// review-helpers.mjs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { matches, target, withHelpers } from './lib.mjs';
import { finding, registerCommonTests, reviewText } from './review-helpers.mjs';

const CASE = 'review-ignores-a-stricter-protecting-test';
const CHANGE = '2026-11-01-vip-greeting';

const clean = reviewText({
  findings: [finding('F1', { severity: 'low', repo: 'app', concern: 'bug', file: 'greet.sh', line: 6 }, 'T1: 1 new behavior: a vip visitor with a name is greeted as a guest of honor. The two stricter existing tests protect the other greetings and are not new behaviors.')],
});

const { workspace } = registerCommonTests(CASE, {
  change: CHANGE,
  review: clean,
  repos: { 'product-repository-unchanged': 'app' },
});

test(`${CASE}: the section grader needs a committed review, so a run that writes nothing does not pass`, () => {
  const dir = workspace();
  assert.ok(matches(CASE, 'review-section-exists', target(dir, CASE, 'review-section-exists')), 'the passing review has its section');
  assert.ok(!matches(CASE, 'review-section-exists', ''), 'no review, no section');
});

test(`${CASE}: no-cap-finding passes on reviews that count one behavior or never mention the cap`, () => {
  const dir = workspace();
  assert.ok(!matches(CASE, 'no-cap-finding', target(dir, CASE, 'no-cap-finding')), 'the review a passing run leaves');
  for (const text of [
    'T1: 1 new behaviors: a vip visitor with a name is greeted as a guest of honor.',
    'The task stays within the light track and needs no split.',
    'The two stricter tests are protecting tests, not new behaviors, so the fix did not go past the cap.',
    'No finding that the fix exceeded the cap.',
    'This is not over the cap.',
    'A single task, well inside the three-task limit.',
  ]) {
    assert.ok(!matches(CASE, 'no-cap-finding', text), `a clean review is flagged: ${text}`);
  }
});

test(`${CASE}: no-cap-finding fires on a review that reports the fix past the cap`, () => {
  for (const text of [
    "T1 adds three new behaviors, so the fix went past the light track's cap (rule 3) and needed the full pipeline.",
    'The task is over the cap: three tests fail in its test commit.',
    'The three-task cap is exceeded by this task.',
    'This change needed the full pipeline.',
    'The fix breached the cap.',
  ]) {
    assert.ok(matches(CASE, 'no-cap-finding', text), `a cap finding is missed: ${text}`);
  }
});

test(`${CASE}: the fixture holds the incident, three tests failing in the test commit and one new behavior`, () => {
  const dir = workspace();
  const failing = withHelpers(
    dir,
    `sha=$(git -C app log --format=%h --grep='^Plan-Task: T1$' | tail -1); d=$(mktemp -d); git -C app archive "$sha" | tar -x -C "$d"; (cd "$d" && bash test.sh || true) | grep -c '^FAIL'`,
  );
  assert.equal(failing.trim(), '3', 'three tests fail in the test commit');
  assert.equal(withHelpers(dir, 'cd app && bash test.sh >/dev/null && echo green').trim(), 'green', 'the change builds green');
  assert.match(withHelpers(dir, `cat "$(copy_of ${CHANGE})/intent/demo/${CHANGE}/intent.md"`), /"track": "fix"/, 'the change is on the light track');
});
