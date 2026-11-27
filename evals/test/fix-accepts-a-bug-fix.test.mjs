// Self-test for fix-accepts-a-bug-fix (LLM half): each grader is tried on what a good draft leaves and on what a bad
// run leaves. The typed accept is the parity half and is not graded here.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, matches, read, sh, target } from './lib.mjs';
import { commonTests, edit, folderOf, reflogOf, withFix } from './fix-lib.mjs';

const CASE = 'fix-accepts-a-bug-fix';
const CHANGE = '2026-11-01-greeting-space';
const folder = folderOf(CHANGE);
const text = (ws, grader) => target(ws, CASE, grader);
const FILE_GRADERS = [
  'drafted-through-the-program', 'intent-is-on-the-fix-track', 'intent-names-the-root-cause-at-its-line', 'spec-reproduces-the-bug',
  'spec-is-a-draft', 'plan-task-changes-the-greeting-script', 'plan-test-fails-before-the-change', 'plan-is-a-draft',
  'product-code-unedited', 'product-repository-unchanged',
];

commonTests(CASE, { fires: ['fix', 'start'] });

test(`${CASE}: a good run's draft passes every file grader, and the fixture as built (no run) passes only the product ones`, () => {
  const none = fixture(CASE);
  for (const g of FILE_GRADERS.filter((g) => !g.startsWith('product-'))) assert.ok(!matches(CASE, g, text(none, g)), `${g} must fail with no run`);
  for (const g of ['product-code-unedited', 'product-repository-unchanged']) assert.ok(matches(CASE, g, text(none, g)), `${g} holds on the fixture`);
  assert.ok(matches(CASE, 'accepted-nothing', text(none, 'accepted-nothing') + 'no commits') === false, 'nothing accepted on the fixture');

  const ws = fixture(CASE);
  withFix(ws, `draft_fix ${CHANGE}`);
  for (const g of FILE_GRADERS) assert.ok(matches(CASE, g, text(ws, g)), `${g} must pass on a good draft:\n${text(ws, g).slice(0, 400)}`);
  assert.ok(!matches(CASE, 'accepted-nothing', text(ws, 'accepted-nothing')), 'a draft accepts nothing');
});

test(`${CASE}: accepted-nothing fires on an acceptance, which is what a model-side accept would leave`, () => {
  const ws = fixture(CASE);
  withFix(ws, `draft_fix ${CHANGE}; accept_fix ${CHANGE}`);
  assert.ok(matches(CASE, 'accepted-nothing', text(ws, 'accepted-nothing')));
  assert.ok(!matches(CASE, 'spec-is-a-draft', text(ws, 'spec-is-a-draft')), 'an accepted spec is not a draft');
  assert.ok(!matches(CASE, 'plan-is-a-draft', text(ws, 'plan-is-a-draft')));
});

test(`${CASE}: the intent graders fail on an intent with another track, and on a root cause that names no line`, () => {
  const ws = fixture(CASE);
  withFix(ws, `draft_fix ${CHANGE}`);
  edit(ws, `${folder}/intent.md`, (t) => t.replace('"track": "fix"', '"track": "feat"'));
  assert.ok(!matches(CASE, 'intent-is-on-the-fix-track', text(ws, 'intent-is-on-the-fix-track')));
  edit(ws, `${folder}/intent.md`, (t) => t.replace('app/greet.sh:4', 'the greeting script'));
  assert.ok(!matches(CASE, 'intent-names-the-root-cause-at-its-line', text(ws, 'intent-names-the-root-cause-at-its-line')));
  // The same place named another way still counts; another line does not.
  const g = 'intent-names-the-root-cause-at-its-line';
  assert.ok(matches(CASE, g, 'The root cause is in greet.sh line 4.'));
  assert.ok(matches(CASE, g, 'Root cause: line 4 of app/greet.sh builds the greeting without a space.'));
  assert.ok(matches(CASE, g, 'greet.sh:4'));
  assert.ok(!matches(CASE, g, 'greet.sh:14'));
  assert.ok(!matches(CASE, g, 'greet.sh builds the greeting without a space.'));
});

test(`${CASE}: the spec and plan graders fail on a spec that doesn't reproduce the bug and a plan whose test can't fail first`, () => {
  const ws = fixture(CASE);
  withFix(ws, `draft_fix ${CHANGE}`);
  edit(ws, `${folder}/spec.md`, (t) => t.replaceAll('Mina', 'Zed'));
  assert.ok(!matches(CASE, 'spec-reproduces-the-bug', text(ws, 'spec-reproduces-the-bug')));
  edit(ws, `${folder}/plan.md`, (t) => t.replace('"files": ["greet.sh", "test.sh"]', '"files": ["test.sh"]'));
  assert.ok(!matches(CASE, 'plan-task-changes-the-greeting-script', text(ws, 'plan-task-changes-the-greeting-script')));
  edit(ws, `${folder}/plan.md`, (t) => t.replace(/It fails before the change because[^\n]*/, 'Run it afterwards.'));
  assert.ok(!matches(CASE, 'plan-test-fails-before-the-change', text(ws, 'plan-test-fails-before-the-change')));
  // A failing mention outside the Tests section of the task doesn't count.
  edit(ws, `${folder}/plan.md`, (t) => t.replace('Run it afterwards.', 'Run it afterwards.\n\n#### Done when\n\nIt must not fail.'));
  assert.ok(!matches(CASE, 'plan-test-fails-before-the-change', text(ws, 'plan-test-fails-before-the-change')));
});

test(`${CASE}: the product graders fail once the model edits the app's code or commits in the app`, () => {
  const ws = fixture(CASE);
  sh(ws, 'cd app && sed -i.bak \'s/Welcome back${2:+,$2}/Welcome back${2:+, $2}/\' greet.sh && rm greet.sh.bak');
  assert.ok(!matches(CASE, 'product-code-unedited', text(ws, 'product-code-unedited')), 'an edited greet.sh');
  assert.ok(matches(CASE, 'product-repository-unchanged', text(ws, 'product-repository-unchanged')), 'uncommitted, so the reflog is the same');
  withFix(ws, 'cd app && git_in commit -q -a -m "fix: put a space after the comma"');
  assert.ok(!matches(CASE, 'product-repository-unchanged', text(ws, 'product-repository-unchanged')), 'a commit in the app');
});

test(`${CASE}: the paths the graders read are where a started change keeps its documents and its reflog`, () => {
  // file targets can't be evaluated against a model run in node; this pins the paths against the engine's own layout.
  const ws = fixture(CASE);
  withFix(ws, `draft_fix ${CHANGE}`);
  for (const doc of ['intent.md', 'spec.md', 'plan.md']) assert.ok(existsSync(join(ws, folder, doc)), doc);
  assert.ok(existsSync(join(ws, reflogOf(CHANGE))));
  assert.match(read(ws, reflogOf(CHANGE)), /\tcommit: docs\(demo\): draft fix 2026-11-01-greeting-space\n$/);
});
