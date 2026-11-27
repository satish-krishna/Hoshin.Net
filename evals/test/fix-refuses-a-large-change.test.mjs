// Self-test for fix-refuses-a-large-change: each grader is tried on what a refusal leaves and on what a run that
// drafted the feature leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, matches, target } from './lib.mjs';
import { commonTests, folderOf, withFix } from './fix-lib.mjs';

const CASE = 'fix-refuses-a-large-change';
const CHANGE = '2026-11-01-visitor-profile';

commonTests(CASE, { fires: ['fix', 'start'] });

test(`${CASE}: the task-cap grader sees the cap named in several phrasings, and not a refusal that never says what the cap is`, () => {
  const g = 'output-names-the-task-cap';
  for (const good of [
    'The light track allows at most three tasks, and this needs about six.',
    'This is more than 3 tasks, so it is not a quick fix.',
    'The task cap for a light-track fix is three, and a profile with export and delete is over it.',
    'A fix is limited to 3 new behaviors, and this is four.',
    'Over the limit of three tasks.',
  ]) assert.ok(matches(CASE, g, good), good);
  for (const bad of ['This is too big for the light track.', 'It is a feature, not a fix, so use the full pipeline.', 'There are three visitors in the greeting rule.']) {
    assert.ok(!matches(CASE, g, bad), bad);
  }
});

test(`${CASE}: the intent grader sees the command, the skill, or the pipeline, and not a refusal that points nowhere`, () => {
  const g = 'output-points-to-intent';
  for (const good of ['Run /hoshin:intent to capture it.', 'Use hoshin:intent.', 'Start with `intent` and the idea.', 'Use the intent skill.', 'Take it through the full pipeline.']) {
    assert.ok(matches(CASE, g, good), good);
  }
  assert.ok(!matches(CASE, g, 'This is too large for a quick fix. Please plan it as a proper change.'));
});

test(`${CASE}: the split grader sees a split offered, and not a refusal with only one way out`, () => {
  const g = 'output-offers-a-split';
  for (const good of ['Or split it into separate fixes, one behavior each.', 'I can do the profile name edit as a smaller fix first.', 'Or splitting it, one fix per behavior.', 'Or send separate changes through the light track.']) {
    assert.ok(matches(CASE, g, good), good);
  }
  assert.ok(!matches(CASE, g, 'Run /hoshin:intent to take it through the full pipeline.'));
});

test(`${CASE}: the master's reflog passes as built, and fails once anything is committed on main`, () => {
  const ws = fixture(CASE);
  assert.ok(matches(CASE, 'master-main-gains-no-commit', target(ws, CASE, 'master-main-gains-no-commit')));
  withFix(ws, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft intent x"');
  assert.ok(!matches(CASE, 'master-main-gains-no-commit', target(ws, CASE, 'master-main-gains-no-commit')));
});

test(`${CASE}: a change started and left behind is what the file graders look for`, () => {
  // file_exists is judged by the runner; this pins that a started change leaves the loose ref the grader's path names,
  // and that a drafted fix leaves the intent.md and spec.md the other two paths name.
  const ws = fixture(CASE);
  assert.equal(withFix(ws, 'ls master/.git/refs/heads/').trim(), 'main', 'the fixture holds no change branch');
  withFix(ws, `draft_fix ${CHANGE}`);
  assert.equal(withFix(ws, 'ls master/.git/refs/heads/change/').trim(), CHANGE);
  assert.ok(existsSync(join(ws, folderOf(CHANGE), 'intent.md')));
  assert.ok(existsSync(join(ws, folderOf(CHANGE), 'spec.md')));
});
