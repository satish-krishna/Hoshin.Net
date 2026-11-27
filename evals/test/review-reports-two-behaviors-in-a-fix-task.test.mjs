// Self-test for review-reports-two-behaviors-in-a-fix-task. The graders every review case shares come from
// review-helpers.mjs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { matches, target, withHelpers } from './lib.mjs';
import { finding, registerCommonTests, reviewText } from './review-helpers.mjs';

const CASE = 'review-reports-two-behaviors-in-a-fix-task';
const CHANGE = '2026-11-01-vip-staff-greeting';

const capFinding = (extra = {}, text) =>
  finding(
    'F1',
    { severity: 'medium', repo: 'app', concern: 'spec', file: 'test.sh', line: 11, ...extra },
    text ?? "T1 establishes two new behaviors (a vip visitor's greeting and a staff visitor's greeting), so the fix went past the light track's cap (rule 3) and needed the full pipeline. Tests counted: a vip visitor with a name is greeted as a guest of honor, a staff visitor with a name is greeted as staff.",
  );

const { workspace } = registerCommonTests(CASE, {
  change: CHANGE,
  review: reviewText({ findings: [capFinding()] }),
  repos: { 'product-repository-unchanged': 'app' },
});

test(`${CASE}: the cap grader sees the finding in the committed review and in the phrasings a correct review uses`, () => {
  const dir = workspace();
  assert.ok(matches(CASE, 'cap-finding-recorded', target(dir, CASE, 'cap-finding-recorded')));
  for (const text of [
    "The fix went past the light track's cap and needed the full pipeline.",
    'Task T1 is over the cap: it has two new behaviors.',
    'T1 exceeds the three-task cap.',
    'T1 establishes two new behaviors, which breaks rule 3 of the triage.',
    'T1 has more than one new behavior; the light track allows one new behavior per task.',
    'The fix breached the light-track limit.',
  ]) {
    assert.ok(matches(CASE, 'cap-finding-recorded', text), `a correct finding is missed: ${text}`);
  }
});

test(`${CASE}: the cap grader fails on a review that finds nothing about the count, or says the count is fine`, () => {
  for (const text of [
    'The vip and staff greetings are implemented as planned.',
    'T1: 1 new behavior. The fix did not go past the cap.',
    'No finding: the fix is not over the cap.',
    'The change never needed the full pipeline.',
    'The convention header is present on every script.',
  ]) {
    assert.ok(!matches(CASE, 'cap-finding-recorded', text), `a clean review passes the cap grader: ${text}`);
  }
});

test(`${CASE}: the finding must be a spec finding for app on the test or the plan, not a note in another concern`, () => {
  const ok = (f) => matches(CASE, 'cap-finding-is-a-spec-finding-on-app', reviewText({ findings: [f] }));
  assert.ok(ok(capFinding()));
  assert.ok(ok(capFinding({ file: `intent/demo/${CHANGE}/plan.md`, line: 20 })), 'a finding at the plan is the same finding');
  assert.ok(ok(capFinding({ file: 'app/test.sh' })));
  assert.ok(!ok(capFinding({ concern: 'bug' })), 'a bug finding is not the cap finding');
  assert.ok(!ok(capFinding({ concern: 'convention', rule: 'CONTRIBUTING.md:5' })), 'a convention finding is not the cap finding');
  assert.ok(!ok(capFinding({ repo: 'lib' })), 'the task is in app');
  assert.ok(!ok(capFinding({ file: 'greet.sh' })), 'the count is read from the tests and the plan, not the production script');
  assert.ok(!matches(CASE, 'cap-finding-is-a-spec-finding-on-app', reviewText()), 'a review with no finding');
});

test(`${CASE}: the fixture holds two new behaviors in one task, two tests failing in its test commit`, () => {
  const dir = workspace();
  const failing = withHelpers(
    dir,
    `sha=$(git -C app log --format=%h --grep='^Plan-Task: T1$' | tail -1); d=$(mktemp -d); git -C app archive "$sha" | tar -x -C "$d"; (cd "$d" && bash test.sh || true) | grep -c '^FAIL'`,
  );
  assert.equal(failing.trim(), '2', 'two tests fail in the test commit');
  assert.equal(withHelpers(dir, 'cd app && bash test.sh >/dev/null && echo green').trim(), 'green', 'the change builds green');
  const plan = withHelpers(dir, `cat "$(copy_of ${CHANGE})/intent/demo/${CHANGE}/plan.md"`);
  assert.match(plan, /"covers": \["R1\.1", "R2\.1"\]/, 'one task covers both scenarios');
  assert.match(plan, /### T1:/);
  assert.doesNotMatch(plan, /### T2:/, 'there is one task');
});
