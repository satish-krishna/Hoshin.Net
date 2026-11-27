// Self-test for review-reads-red-from-the-program. The graders every review case shares come from review-helpers.mjs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { matches, withHelpers } from './lib.mjs';
import { finding, registerCommonTests, reviewText } from './review-helpers.mjs';

const CASE = 'review-reads-red-from-the-program';
const CHANGE = '2026-11-01-vip-greeting';
const BUILD_LOG = `intent/demo/${CHANGE}/build/app.md`;

const redFinding = (extra = {}) =>
  finding('F1', { severity: 'medium', repo: 'app', concern: 'spec', file: BUILD_LOG, line: 12, ...extra }, 'T1: red was not observed (no red recorded for T1 on this machine), so nothing shows the test failed before the code.');

const { workspace } = registerCommonTests(CASE, {
  change: CHANGE,
  review: reviewText({ findings: [redFinding()] }),
  repos: { 'product-repository-unchanged': 'app' },
});

test(`${CASE}: the red grader wants a medium spec finding for app at the build log's red line, line 12`, () => {
  const ok = (f) => matches(CASE, 'red-finding-points-at-the-red-line', reviewText({ findings: [f] }));
  assert.ok(ok(redFinding()));
  assert.ok(ok(redFinding({ file: 'build/app.md' })), 'a path relative to the change folder is the same file');
  assert.ok(ok(redFinding({ file: `.hoshin/copies/${CHANGE}/${BUILD_LOG}` })), 'a path from the workspace is the same file');
  const oneLine = `\`\`\`jsonc\n{ "id": "F1", "severity": "medium", "repo": "app", "concern": "spec", "file": "${BUILD_LOG}", "line": 12 }\n\`\`\``;
  assert.ok(matches(CASE, 'red-finding-points-at-the-red-line', oneLine), 'the line last, on one line');
  // What a failing run leaves.
  assert.ok(!ok(redFinding({ line: 7 })), 'the entry heading is not the red line (it is where read --json puts the entry)');
  assert.ok(!ok(redFinding({ line: 11 })), 'the commits line');
  assert.ok(!ok(redFinding({ line: 120 })), 'a line that only starts with 12');
  assert.ok(!ok(redFinding({ file: 'greet.sh' })), 'a finding in the product code is not about the log');
  assert.ok(!ok(redFinding({ file: `intent/demo/${CHANGE}/build/lib.md` })), 'another log');
  assert.ok(!ok(redFinding({ severity: 'low' })), 'red not observed is medium');
  assert.ok(!ok(redFinding({ severity: 'high' })), 'red not observed is medium');
  assert.ok(!ok(redFinding({ concern: 'bug' })), 'it is a spec finding');
  assert.ok(!ok(redFinding({ repo: 'lib' })), 'the task is in app');
  assert.ok(!matches(CASE, 'red-finding-points-at-the-red-line', reviewText()), 'a review with no finding');
});

test(`${CASE}: the red line is line 12 of the log the fixture wrote, and the program reads the entry's red from it`, () => {
  const dir = workspace();
  const log = withHelpers(dir, `cat "$(copy_of ${CHANGE})/${BUILD_LOG}"`);
  const lines = log.replace(/\r\n/g, '\n').split('\n');
  assert.match(lines[11], /^\s*"red": "not observed: no red recorded for T1 on this machine" \}$/, 'line 12 is the red line');
  assert.match(lines[6], /^### T1$/, 'line 7 is the entry heading, which is the only line the program reports for an entry');
  const read = withHelpers(dir, `cd master && hoshin read --json "$(copy_of ${CHANGE})/${BUILD_LOG}"`);
  assert.match(read, /"red":"not observed: no red recorded for T1 on this machine"/);
});

test(`${CASE}: the reason grader sees why red was not observed, and not a review that says nothing of it`, () => {
  assert.ok(matches(CASE, 'red-finding-gives-the-reason', 'T1: red was not observed, so the test was never seen to fail.'));
  assert.ok(matches(CASE, 'red-finding-gives-the-reason', 'The log says: not observed: no red recorded for T1 on this machine.'));
  assert.ok(matches(CASE, 'red-finding-gives-the-reason', 'No red recorded for this task.'));
  assert.ok(!matches(CASE, 'red-finding-gives-the-reason', 'The convention header is missing from vip.sh.'));
});
