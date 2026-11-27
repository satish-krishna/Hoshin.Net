// Self-test for fix-follows-written-rules: each grader is tried on what a run that read the app's written rules leaves
// and on what a run that never followed the link leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, matches, read, target } from './lib.mjs';
import { commonTests, edit, folderOf, reflogOf, withFix } from './fix-lib.mjs';

const CASE = 'fix-follows-written-rules';
const CHANGE = '2026-11-01-greeting-space';
const folder = folderOf(CHANGE);
const text = (ws, grader) => target(ws, CASE, grader);

commonTests(CASE, { fires: ['fix', 'start'] });

test(`${CASE}: the fixture holds the written rule behind a link, and a current context file`, () => {
  const ws = fixture(CASE);
  assert.match(read(ws, 'app/CONTRIBUTING.md'), /\(docs\/changes\.md\)/);
  assert.match(read(ws, 'app/docs/changes.md'), /Unreleased.*CHANGELOG\.md/);
  assert.ok(existsSync(join(ws, 'app', 'CHANGELOG.md')));
  const head = withFix(ws, 'git -C app rev-parse HEAD').trim();
  assert.ok(read(ws, 'master/intent/demo/context/app.md').includes(head), 'source_commit is the app head');
  assert.ok(matches(CASE, 'product-repository-unchanged', text(ws, 'product-repository-unchanged')));
});

test(`${CASE}: a plan whose task lists CHANGELOG.md passes, and a plan that only mentions it in prose, or never does, fails`, () => {
  const ws = fixture(CASE);
  withFix(ws, `draft_fix ${CHANGE}`);
  assert.ok(!matches(CASE, 'a-task-adds-the-changelog-line', text(ws, 'a-task-adds-the-changelog-line')), 'a plan that never followed the link');
  edit(ws, `${folder}/plan.md`, (t) => t.replace('Run `bash test.sh`.', 'Run `bash test.sh`. Add a line to CHANGELOG.md.'));
  assert.ok(!matches(CASE, 'a-task-adds-the-changelog-line', text(ws, 'a-task-adds-the-changelog-line')), 'CHANGELOG.md in prose only');
  edit(ws, `${folder}/plan.md`, (t) => t.replace('"files": ["greet.sh", "test.sh"]', '"files": ["greet.sh", "test.sh", "CHANGELOG.md"]'));
  assert.ok(matches(CASE, 'a-task-adds-the-changelog-line', text(ws, 'a-task-adds-the-changelog-line')), 'CHANGELOG.md in the files list');
  edit(ws, `${folder}/plan.md`, (t) => t.replace('"files": ["greet.sh", "test.sh", "CHANGELOG.md"]', '"files": [\n    "greet.sh",\n    "CHANGELOG.md"\n  ]'));
  assert.ok(matches(CASE, 'a-task-adds-the-changelog-line', text(ws, 'a-task-adds-the-changelog-line')), 'a files list written over several lines');
});

test(`${CASE}: the draft stays on the light track and nothing is accepted`, () => {
  const none = fixture(CASE);
  assert.ok(!matches(CASE, 'stayed-on-the-light-track', text(none, 'stayed-on-the-light-track')), 'no change started, no draft');
  const ws = fixture(CASE);
  withFix(ws, `draft_fix ${CHANGE}`);
  assert.ok(matches(CASE, 'stayed-on-the-light-track', text(ws, 'stayed-on-the-light-track')));
  assert.ok(!matches(CASE, 'accepted-nothing', text(ws, 'accepted-nothing')));
  withFix(ws, `accept_fix ${CHANGE}`);
  assert.ok(matches(CASE, 'accepted-nothing', text(ws, 'accepted-nothing')), 'an acceptance is what the grader forbids');
  assert.match(read(ws, reflogOf(CHANGE)), /draft fix/);
});

test(`${CASE}: the product repository grader fails once the app gains a commit`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'cd app && echo "- Fixed the greeting." >> CHANGELOG.md && git_in commit -q -a -m "docs: changelog"');
  assert.ok(!matches(CASE, 'product-repository-unchanged', text(ws, 'product-repository-unchanged')));
});
