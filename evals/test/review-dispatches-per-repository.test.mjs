// Self-test for review-dispatches-per-repository. The workspace is the base one plus a second product repository, lib,
// added to system demo in the marker. The graders every review case shares come from review-helpers.mjs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { graders, matches, target, withHelpers } from './lib.mjs';
import { finding, registerCommonTests, reviewText } from './review-helpers.mjs';

const CASE = 'review-dispatches-per-repository';
const CHANGE = '2026-11-01-vip-greeting-and-farewell';

const appFinding = (id = 'F1', extra = {}) =>
  finding(id, { severity: 'high', repo: 'app', concern: 'convention', rule: 'CONTRIBUTING.md:5', file: 'vip.sh', line: 1, ...extra }, 'vip.sh has no header (CONTRIBUTING.md:5).');
const libFinding = (id = 'F2', extra = {}) =>
  finding(id, { severity: 'high', repo: 'lib', concern: 'convention', rule: 'CONTRIBUTING.md:5', file: 'farewell.sh', line: 2 , ...extra }, 'farewell.sh has no usage comment (CONTRIBUTING.md:5).');
const merged = (findings, extra = {}) => reviewText({ commits: { app: 'abc1234', lib: 'def5678' }, findings, ...extra });

const { workspace } = registerCommonTests(CASE, {
  change: CHANGE,
  review: merged([appFinding(), libFinding()]),
  repos: { 'app-repository-unchanged': 'app', 'lib-repository-unchanged': 'lib' },
});

const agent = (description, prompt) => JSON.stringify({ description, subagent_type: 'general-purpose', prompt });

test(`${CASE}: three Agent calls are required, one for each repository and one across them`, () => {
  const g = graders(CASE);
  assert.equal(g['three-reviewers-dispatched'].min, '3');
  assert.equal(g['three-reviewers-dispatched'].max, '3');
  assert.ok(matches(CASE, 'three-reviewers-dispatched', agent('Review app', 'Review the app repository.')), 'any Agent call counts toward the three');
  assert.ok(!matches(CASE, 'three-reviewers-dispatched', ''), 'an empty input is not a call');
});

test(`${CASE}: each reviewer grader finds its own repository in the calls, and fails when a repository was skipped`, () => {
  const calls = [agent('Review app', 'Review the change in the app repository: range main..change.'), agent('Review lib', 'Review the change in the lib repository.'), agent('Review across repositories', 'Check the seams between app and lib.')];
  assert.ok(calls.some((c) => matches(CASE, 'a-reviewer-for-app', c)));
  assert.ok(calls.some((c) => matches(CASE, 'a-reviewer-for-lib', c)));
  assert.ok(calls.some((c) => matches(CASE, 'a-reviewer-across-repositories', c)));
  // A run that reviewed app twice and never lib, or never looked across, fails the matching grader.
  const skipped = [agent('Review app', 'Review the app repository.'), agent('Review app again', 'Review the app repository.')];
  assert.ok(!skipped.some((c) => matches(CASE, 'a-reviewer-for-lib', c)), 'lib was never reviewed');
  assert.ok(!skipped.some((c) => matches(CASE, 'a-reviewer-across-repositories', c)), 'nothing was reviewed across the repositories');
  assert.ok(!matches(CASE, 'a-reviewer-for-app', agent('Review lib', 'Review the library repository.')), 'application is not app');
});

test(`${CASE}: the merged review is one appended section naming both repositories, and the committed passing review satisfies the graders`, () => {
  const dir = workspace();
  for (const grader of ['one-appended-section', 'header-names-both-repositories', 'app-convention-finding-cites-its-rule', 'lib-convention-finding-cites-its-rule', 'review-commit-landed']) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on the review a passing run leaves`);
  }
  for (const grader of ['no-second-section', 'finding-numbers-are-not-repeated']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} fires on the review a passing run leaves`);
  }
  // Not merged: a section per reviewer, or a header that names one repository.
  assert.ok(matches(CASE, 'no-second-section', merged([], { sections: 2 })), 'each reviewer wrote its own section');
  assert.ok(!matches(CASE, 'header-names-both-repositories', reviewText({ commits: { app: 'abc1234' }, findings: [appFinding()] })), 'a header for app alone');
  assert.ok(!matches(CASE, 'header-names-both-repositories', reviewText({ commits: { lib: 'def5678' } })), 'a header for lib alone');
  assert.ok(matches(CASE, 'header-names-both-repositories', reviewText({ commits: { lib: 'def5678', app: 'abc1234' } })), 'either order');
});

test(`${CASE}: each repository's finding cites that repository's own rule and file, and a finding in the wrong repository fails`, () => {
  const okApp = (findings) => matches(CASE, 'app-convention-finding-cites-its-rule', merged(findings));
  const okLib = (findings) => matches(CASE, 'lib-convention-finding-cites-its-rule', merged(findings));
  assert.ok(okApp([appFinding(), libFinding()]));
  assert.ok(okLib([appFinding(), libFinding()]));
  assert.ok(okLib([libFinding('F2', { file: 'lib/farewell.sh', rule: 'CONTRIBUTING.md:6' })]));
  // Only one reviewer's findings survived the merge.
  assert.ok(!okLib([appFinding()]), 'lib has no finding');
  assert.ok(!okApp([libFinding()]), 'app has no finding');
  // The reviewers swapped repositories, or cited the other repository's file.
  assert.ok(!okLib([libFinding('F2', { repo: 'app' })]), 'the lib finding filed under app');
  assert.ok(!okApp([appFinding('F1', { repo: 'lib' })]), 'the app finding filed under lib');
  assert.ok(!okLib([libFinding('F2', { file: 'vip.sh' })]), "lib's finding on app's script");
  assert.ok(!okApp([appFinding('F1', { file: 'farewell.sh' })]), "app's finding on lib's script");
  assert.ok(!okLib([libFinding('F2', { concern: 'bug' })]), 'not a convention finding');
  assert.ok(!okApp([appFinding('F1', { rule: 'STYLE.md:5' })]), 'not the written rule');
});

test(`${CASE}: finding numbers run on across the merge, and two reviewers' F1 are caught`, () => {
  assert.ok(!matches(CASE, 'finding-numbers-are-not-repeated', merged([appFinding('F1'), libFinding('F2')])));
  assert.ok(!matches(CASE, 'finding-numbers-are-not-repeated', merged([appFinding('F1'), libFinding('F2'), appFinding('F3', { line: 4 })])));
  assert.ok(matches(CASE, 'finding-numbers-are-not-repeated', merged([appFinding('F1'), libFinding('F1')])), 'both reviewers started at F1');
  assert.ok(matches(CASE, 'finding-numbers-are-not-repeated', merged([appFinding('F1'), libFinding('F2'), libFinding('F2')])));
});

test(`${CASE}: the fixture holds two repositories, each breaking its own rule and each building green`, () => {
  const dir = workspace();
  assert.match(withHelpers(dir, 'cd master && cat .hoshin.json'), /"repos": \["app", "lib"\]/, 'the marker lists both repositories');
  assert.match(withHelpers(dir, 'cd app && bash scripts/lint.sh || true'), /vip\.sh: missing the script header \(CONTRIBUTING\.md:5\)/);
  assert.match(withHelpers(dir, 'cd lib && bash scripts/lint.sh || true'), /farewell\.sh: missing the script header \(CONTRIBUTING\.md:5\)/);
  assert.notEqual(withHelpers(dir, 'cd app && sed -n 5p CONTRIBUTING.md'), withHelpers(dir, 'cd lib && sed -n 5p CONTRIBUTING.md'), 'the two repositories have different written rules');
  assert.equal(withHelpers(dir, 'cd app && bash test.sh >/dev/null && echo green').trim(), 'green');
  assert.equal(withHelpers(dir, 'cd lib && bash test.sh >/dev/null && echo green').trim(), 'green');
  const state = withHelpers(dir, `cd master && hoshin state --json --system demo --change ${CHANGE}`);
  assert.match(state, /"app":\{"all":\["T1"\],"logged":\["T1"\]/);
  assert.match(state, /"lib":\{"all":\["T2"\],"logged":\["T2"\]/);
});
