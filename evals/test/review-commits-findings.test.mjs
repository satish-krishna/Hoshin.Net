// Self-test for review-commits-findings: each grader is tried on what a passing run leaves and on what a failing run
// leaves. The graders every review case shares are registered by review-helpers.mjs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { matches, target, withHelpers } from './lib.mjs';
import { finding, registerCommonTests, reviewPath, reviewText } from './review-helpers.mjs';

const CASE = 'review-commits-findings';
const CHANGE = '2026-11-01-vip-greeting';

const convention = (extra = {}) =>
  finding('F1', { severity: 'high', repo: 'app', concern: 'convention', rule: 'CONTRIBUTING.md:5', file: 'vip.sh', line: 1, ...extra }, 'vip.sh has no header: the lint script fails on it (CONTRIBUTING.md:5).');

const { workspace } = registerCommonTests(CASE, {
  change: CHANGE,
  review: reviewText({ findings: [convention()] }),
  repos: { 'product-repository-unchanged': 'app' },
});

test(`${CASE}: the review document graders read the file the run leaves, and the committed passing review satisfies them`, () => {
  // registerCommonTests committed a passing review into this workspace.
  const dir = workspace();
  for (const grader of ['review-is-layout-2', 'review-section-exists', 'findings-name-the-repository', 'convention-finding-cites-the-written-rule']) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on the review a passing run leaves`);
  }
});

test(`${CASE}: the layout and section graders fail on a review in the original's layout or without a section`, () => {
  const old = '---\nlayout: 2\n---\n\n# Review findings\n\n## Review 1\n';
  assert.ok(!matches(CASE, 'review-is-layout-2', old), 'layout 2 in YAML front matter is not a jsonc block');
  assert.ok(!matches(CASE, 'review-is-layout-2', '```jsonc\n{ "review": 1 }\n```'));
  assert.ok(!matches(CASE, 'review-section-exists', '# Review findings\n\n## Review 2\n'));
  assert.ok(!matches(CASE, 'review-section-exists', '# Review findings\n\n### Review 1\n'));
  assert.ok(matches(CASE, 'review-section-exists', reviewText()));
});

test(`${CASE}: the repository grader wants app named, and a review that names another repository fails`, () => {
  assert.ok(matches(CASE, 'findings-name-the-repository', reviewText({ findings: [convention()] })));
  assert.ok(!matches(CASE, 'findings-name-the-repository', reviewText({ findings: [convention({ repo: 'lib' })] })));
  assert.ok(!matches(CASE, 'findings-name-the-repository', reviewText()), 'a review with no finding names no repository');
});

test(`${CASE}: the convention grader wants one finding that is a convention finding, for app, citing CONTRIBUTING.md line 5 or 6, on vip.sh`, () => {
  const ok = (f) => matches(CASE, 'convention-finding-cites-the-written-rule', reviewText({ findings: [f] }));
  assert.ok(ok(convention()));
  assert.ok(ok(convention({ rule: 'CONTRIBUTING.md:6' })));
  assert.ok(ok(convention({ rule: 'CONTRIBUTING.md:5-6' })));
  assert.ok(ok(convention({ file: 'app/vip.sh', line: 2 })), 'a path with the repository in front still names vip.sh');
  // The keys in another order are the same finding.
  assert.ok(matches(CASE, 'convention-finding-cites-the-written-rule', '```jsonc\n{ "rule": "CONTRIBUTING.md:5", "file": "vip.sh", "line": 1, "repo": "app", "severity": "medium", "concern": "convention", "id": "F1" }\n```'));
  // What a failing run leaves.
  assert.ok(!ok(convention({ rule: 'CONTRIBUTING.md:1' })), 'a rule line that is not the rule');
  assert.ok(!ok(convention({ rule: 'STYLE.md:5' })), 'another file');
  assert.ok(!ok(convention({ rule: 'scripts/lint.sh:5' })), 'the lint script is not the written rule');
  assert.ok(!ok(convention({ concern: 'bug' })), 'a bug finding is not a convention finding');
  assert.ok(!ok(convention({ repo: 'lib' })), 'the wrong repository');
  assert.ok(!ok(convention({ file: 'greet.sh' })), 'the wrong file: greet.sh follows the rule');
  const noRule = { severity: 'high', repo: 'app', concern: 'convention', file: 'vip.sh', line: 1 };
  assert.ok(!matches(CASE, 'convention-finding-cites-the-written-rule', reviewText({ findings: [finding('F1', noRule)] })), 'a convention finding without a rule');
  assert.ok(!matches(CASE, 'convention-finding-cites-the-written-rule', reviewText()), 'no finding at all');
});

test(`${CASE}: the fields of two different findings are never read as one`, () => {
  const split = reviewText({
    findings: [
      finding('F1', { severity: 'low', repo: 'app', concern: 'convention', rule: 'STYLE.md:9', file: 'greet.sh', line: 3 }),
      finding('F2', { severity: 'low', repo: 'app', concern: 'bug', rule: 'CONTRIBUTING.md:5', file: 'vip.sh', line: 1 }),
    ],
  });
  assert.ok(!matches(CASE, 'convention-finding-cites-the-written-rule', split));
});

test(`${CASE}: the fixture holds what the case promises, a script without the header that the lint script rejects`, () => {
  const dir = workspace();
  assert.match(withHelpers(dir, 'cd app && sed -n 5p CONTRIBUTING.md'), /^Every shell script starts with/);
  assert.match(withHelpers(dir, 'cd app && bash scripts/lint.sh || true'), /vip\.sh: missing the script header \(CONTRIBUTING\.md:5\)/);
  assert.equal(withHelpers(dir, 'cd app && bash test.sh >/dev/null && echo green').trim(), 'green', 'the change builds green');
  assert.ok(reviewPath(CHANGE).endsWith('build/review.md'));
});
