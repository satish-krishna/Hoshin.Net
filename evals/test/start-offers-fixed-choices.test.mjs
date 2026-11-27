// Self-test for start-offers-fixed-choices: each grader is tried on what a passing run leaves and on what a failing run leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, matches, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'start-offers-fixed-choices';
const asked = 'This is a light-track fix: a missing space in app, about 1 task.\n\n1. Take the light track\n2. Take the full pipeline instead\n3. Stop here';

test(`${CASE}: the skill graders see start, and a run of fix`, () => {
  assert.ok(matches(CASE, 'start-fired', JSON.stringify({ skill: 'hoshin:start' })));
  assert.ok(matches(CASE, 'start-fired', skill('start')));
  assert.ok(!matches(CASE, 'start-fired', skill('hoshin:fix')));
  assert.ok(matches(CASE, 'fix-not-run', skill('hoshin:fix')), 'not_contains style: a fix run is what it forbids');
  assert.ok(!matches(CASE, 'fix-not-run', skill('hoshin:start')));
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const ws = fixture(CASE);
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
});

test(`${CASE}: the choices grader needs the light track's three choices, in order, with the exact words`, () => {
  assert.ok(matches(CASE, 'light-track-choices-offered-in-order', asked));
  assert.ok(matches(CASE, 'light-track-choices-offered-in-order', 'Choices: **Take the light track**, **Take the full pipeline instead**, **Stop here**.'));
  assert.ok(!matches(CASE, 'light-track-choices-offered-in-order', asked.replace('3. Stop here', '')), 'no stop');
  assert.ok(!matches(CASE, 'light-track-choices-offered-in-order', '1. Take the light track\n2. Stop here'), 'the pipeline choice missing');
  assert.ok(!matches(CASE, 'light-track-choices-offered-in-order', 'Take the full pipeline instead, take the light track, or stop here.'), 'out of order');
  assert.ok(!matches(CASE, 'light-track-choices-offered-in-order', 'Shall I go on with the light track?'), 'a free-form question');
});

test(`${CASE}: the verdict grader needs the repository and a task estimate`, () => {
  assert.ok(matches(CASE, 'verdict-states-repository-and-estimate', 'A light-track fix in app, about 1 task.'));
  assert.ok(matches(CASE, 'verdict-states-repository-and-estimate', 'The bug is in app; the fix needs about two tasks.'));
  assert.ok(!matches(CASE, 'verdict-states-repository-and-estimate', 'A light-track fix in app.'), 'no estimate');
  assert.ok(!matches(CASE, 'verdict-states-repository-and-estimate', 'A light-track fix, about 1 task.'), 'no repository');
});

test(`${CASE}: the role grader fires on a question about the role and not on a sentence that mentions the roles`, () => {
  for (const asking of ['Who is running this change: an engineer or a product owner?', 'Are you the engineer or the product owner?', 'What is your role here?', 'Which role are you in?']) {
    assert.ok(matches(CASE, 'role-never-asked', asking), asking);
  }
  for (const ok of [asked, 'You hold both roles, engineer and product owner, so I run the light track here.', 'Given your roles as engineer and product owner, I route this to the light track.']) {
    assert.ok(!matches(CASE, 'role-never-asked', ok), ok);
  }
});

test(`${CASE}: a stop at the choices leaves main, the change branches and the copies alone, and a started change is what the file graders look for`, () => {
  const ws = fixture(CASE);
  assert.ok(matches(CASE, 'master-gained-no-commit', target(ws, CASE, 'master-gained-no-commit')), 'the fixture\'s own history');
  assert.ok(!existsSync(join(ws, 'master', '.git', 'refs', 'heads', 'change')), 'no change ref yet, so no-change-left-behind holds');
  assert.ok(!existsSync(join(ws, '.hoshin', 'copies')), 'no copy yet, so no-copy-created holds');
  withHelpers(ws, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft fix x"');
  assert.ok(!matches(CASE, 'master-gained-no-commit', target(ws, CASE, 'master-gained-no-commit')), 'a commit on main is seen');
  // file_exists is judged by the runner: pin that a change started through the program leaves what the two graders name.
  withHelpers(ws, 'change_start 2026-11-01-greeting-space fix demo');
  assert.equal(withHelpers(ws, 'ls master/.git/refs/heads/change/').trim(), '2026-11-01-greeting-space');
  assert.ok(existsSync(join(ws, '.hoshin', 'copies')));
});
