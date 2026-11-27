// Self-test for fix-feedback-refused-at-boundary: each grader is tried on what a refusal leaves (the fixture as built)
// and on what a run that redrafted or wrote the web change leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, matches, target } from './lib.mjs';
import { boundaryReplies, commonTests, edit, folderOf, withFix } from './fix-lib.mjs';

const CASE = 'fix-feedback-refused-at-boundary';
const CHANGE = '2026-10-24-greeting-space';
const folder = folderOf(CHANGE);
const text = (ws, grader) => target(ws, CASE, grader);
const LEFT = ['intent-left-as-drafted', 'spec-left-as-drafted', 'plan-left-as-drafted'];

commonTests(CASE, { fires: ['fix'] });

test(`${CASE}: a refusal leaves every file and reflog grader passing, as the fixture stands`, () => {
  const ws = fixture(CASE);
  for (const g of ['change-gained-no-commit', 'master-main-gains-no-commit', ...LEFT]) assert.ok(matches(CASE, g, text(ws, g)), g);
  assert.ok(existsSync(join(ws, folder, 'spec.md')));
  assert.ok(existsSync(join(ws, 'web', 'index.html')), 'web is a repository of the workspace');
});

test(`${CASE}: change-gained-no-commit fails on any commit after the draft, a redraft or an acceptance`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'redraft_with_hyphen');
  assert.ok(!matches(CASE, 'change-gained-no-commit', text(ws, 'change-gained-no-commit')), 'a second draft fix commit');
  const ws2 = fixture(CASE);
  withFix(ws2, 'accept_fix');
  assert.ok(!matches(CASE, 'change-gained-no-commit', text(ws2, 'change-gained-no-commit')), 'acceptances');
});

test(`${CASE}: master-main-gains-no-commit fails once anything is committed on main`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft intent x"');
  assert.ok(!matches(CASE, 'master-main-gains-no-commit', text(ws, 'master-main-gains-no-commit')));
});

test(`${CASE}: the document graders fail on a document that took the web change in, and on one that is no longer a draft`, () => {
  const ws = fixture(CASE);
  for (const [doc, grader] of [['intent.md', LEFT[0]], ['spec.md', LEFT[1]], ['plan.md', LEFT[2]]]) {
    const path = `${folder}/${doc}`;
    const original = text(ws, grader);
    assert.ok(matches(CASE, grader, original), `${doc} as drafted`);
    assert.ok(!matches(CASE, grader, `${original}\nThe landing page in the web repository shows the greeting.\n`), `${doc} with web in it`);
    assert.ok(!matches(CASE, grader, `${original}\nThe greeting is shown in bold.\n`), `${doc} with bold in it`);
    assert.ok(!matches(CASE, grader, original.replace('"status": "draft"', '"status": "accepted"')), `${doc} accepted`);
    assert.ok(!matches(CASE, grader, ''), `${doc} missing`);
    edit(ws, path, (t) => `${t}\nThe landing page in the web repository shows the greeting.\n`);
    assert.ok(!matches(CASE, grader, text(ws, grader)), `${doc} on disk with web in it`);
  }
});

test(`${CASE}: the refusal graders see the one-repository rule and the intent route, and not a refusal that names neither`, () => {
  assert.ok(matches(CASE, 'output-names-the-one-repository-rule', boundaryReplies.good));
  assert.ok(matches(CASE, 'output-points-to-intent', boundaryReplies.good));
  assert.ok(matches(CASE, 'output-names-the-one-repository-rule', 'A light-track fix covers a single repository, and web is another repository.'));
  assert.ok(matches(CASE, 'output-points-to-intent', boundaryReplies.intentSkill));
  assert.ok(!matches(CASE, 'output-names-the-one-repository-rule', boundaryReplies.noRule));
  assert.ok(!matches(CASE, 'output-points-to-intent', boundaryReplies.noIntent));
  assert.ok(matches(CASE, 'output-names-the-one-repository-rule', boundaryReplies.noIntent));
});
