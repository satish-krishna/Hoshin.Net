// Self-test for start-offers-pipeline-choices: each grader is tried on what a passing run leaves and on what a failing run leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, matches, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'start-offers-pipeline-choices';
const asked = "This breaks the light track's one-repository rule.\n\n1. Take the full pipeline\n2. Stop here";

test(`${CASE}: the skill graders see start, and a run of fix`, () => {
  assert.ok(matches(CASE, 'start-fired', JSON.stringify({ skill: 'hoshin:start' })));
  assert.ok(!matches(CASE, 'start-fired', skill('hoshin:intent')));
  assert.ok(matches(CASE, 'fix-not-run', skill('fix')), 'a fix run is what it forbids');
  assert.ok(!matches(CASE, 'fix-not-run', skill('hoshin:start')));
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const ws = fixture(CASE);
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
});

test(`${CASE}: the choices grader needs the pipeline and a stop in order, and the light track must not be offered`, () => {
  assert.ok(matches(CASE, 'full-pipeline-choices-offered-in-order', asked));
  assert.ok(!matches(CASE, 'full-pipeline-choices-offered-in-order', 'Stop here, or take the full pipeline.'), 'out of order');
  assert.ok(!matches(CASE, 'full-pipeline-choices-offered-in-order', 'Take the full pipeline.'), 'no stop');
  // light-track-not-offered is a not_contains grader: it must match what it forbids and not the verdict.
  assert.ok(matches(CASE, 'light-track-not-offered', 'Take the light track'));
  assert.ok(matches(CASE, 'light-track-not-offered', '1. **Take the light track:** I run the fix skill.'), 'an offered choice');
  assert.ok(!matches(CASE, 'light-track-not-offered', asked));
  assert.ok(!matches(CASE, 'light-track-not-offered', "Because of rule 2, it can't take the light track whoever asks, so that option isn't on the list."), 'a refusal explained');
});

test(`${CASE}: the verdict grader names a broken rule in the triage's wording, and not a rule that holds`, () => {
  for (const ok of ['It changes a Business Rule.', 'Rule 2 fails: it changes C1.', 'It needs more than three tasks.', 'About five tasks, so rule 3 fails.', 'It needs about 10 tasks, so rule 3 fails.', 'That is past the three-task cap.', 'The change needs a second repository.', 'It would change a business rule in the context.', 'Rule 1 fails: the change spans two repositories.', 'At most three tasks: fails.', '**One repository:** fails, it needs web as well.', 'No business rule in the context changes: fails.', 'The light track does not fit: the change needs a second repository.', "This isn't small: it needs more than three tasks."]) {
    assert.ok(matches(CASE, 'verdict-names-the-broken-rule', ok), ok);
  }
  for (const no of ['This is a feature in the master repository.', 'It touches the app repository, about three tasks.', 'The change makes no change to a business rule.', 'It changes no business rule, so rule 2 holds.', 'It fits in no more than three tasks.', 'It touches no second repository.', 'Rule 2 holds.', 'It does not need more than three tasks.', 'At most three tasks: holds.', '**One repository:** holds.', 'Rule 3 does not fail.', 'It never changes a business rule.']) {
    assert.ok(!matches(CASE, 'verdict-names-the-broken-rule', no), no);
  }
});

test(`${CASE}: the role grader fires on a question about the role and not on a sentence that mentions the roles`, () => {
  for (const asking of ['Who is running this change?', 'Are you the engineer or the product owner?', 'What is your role here?', 'What role are you playing in this change?', 'Who are you in this change?']) {
    assert.ok(matches(CASE, 'role-never-asked', asking), asking);
  }
  for (const ok of [asked, 'Given your roles as engineer and product owner, I route this to the full pipeline.', 'You hold both roles, so no question about your roles is needed.']) {
    assert.ok(!matches(CASE, 'role-never-asked', ok), ok);
  }
});

test(`${CASE}: a stop at the choices leaves main, the change branches and the copies alone`, () => {
  const ws = fixture(CASE);
  assert.ok(matches(CASE, 'master-gained-no-commit', target(ws, CASE, 'master-gained-no-commit')));
  assert.ok(!existsSync(join(ws, 'master', '.git', 'refs', 'heads', 'change')));
  assert.ok(!existsSync(join(ws, '.hoshin', 'copies')));
  withHelpers(ws, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft intent x"');
  assert.ok(!matches(CASE, 'master-gained-no-commit', target(ws, CASE, 'master-gained-no-commit')));
  withHelpers(ws, 'change_start 2026-11-01-profile feat demo');
  assert.equal(withHelpers(ws, 'ls master/.git/refs/heads/change/').trim(), '2026-11-01-profile');
  assert.ok(existsSync(join(ws, '.hoshin', 'copies')));
});
