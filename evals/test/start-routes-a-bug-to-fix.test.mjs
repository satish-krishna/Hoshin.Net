// Self-test for start-routes-a-bug-to-fix: each grader is tried on what a passing run leaves and on what a failing run leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, read, skill, target, traceLine, withHelpers } from './lib.mjs';
import { CHANGE, copyLog, documentPath, draftFix, fixDocuments } from './start-docs.mjs';

const CASE = 'start-routes-a-bug-to-fix';
const on = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));

test(`${CASE}: the skill graders see start, fix and a refused intent, plain or namespaced`, () => {
  assert.ok(matches(CASE, 'start-fired', skill('hoshin:start')));
  assert.ok(matches(CASE, 'start-fired', skill('start')));
  assert.ok(!matches(CASE, 'start-fired', skill('hoshin:fix')));
  assert.ok(matches(CASE, 'fix-fired', skill('hoshin:fix', `${CHANGE} returning visitors see Welcome back,Mina`)));
  assert.ok(!matches(CASE, 'fix-fired', skill('hoshin:start')));
  assert.ok(matches(CASE, 'intent-never-ran', skill('hoshin:intent')), 'a route to the pipeline is seen');
  assert.ok(!matches(CASE, 'intent-never-ran', skill('hoshin:fix')));
});

test(`${CASE}: the fixture is the greeting bug with a current context file, and the trace grader fails until a hook has run`, () => {
  const ws = fixture(CASE);
  assert.match(read(ws, 'app/greet.sh'), /Welcome back\$\{2:\+,\$2\}/, 'the bug: no space after the comma');
  assert.equal(read(ws, 'app/greet.sh').split('\n')[3], 'echo "Welcome back${2:+,$2}"', 'the reply grader looks for line 4');
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
  for (const grader of ['master-main-gains-no-commit', 'product-repository-unchanged', 'product-code-untouched']) assert.ok(on(ws, grader), `${grader} holds on the fixture as built`);
  assert.ok(!on(ws, 'fix-drafted-in-the-change-copy'), 'no change yet');
});

test(`${CASE}: a draft left by the program satisfies the document, commit and untouched-repository graders`, () => {
  const ws = fixture(CASE);
  draftFix(ws);
  assert.ok(on(ws, 'fix-drafted-in-the-change-copy'));
  assert.ok(on(ws, 'intent-marks-the-light-track'));
  assert.ok(on(ws, 'spec-reproduces-the-bug'));
  assert.ok(on(ws, 'plan-names-the-root-cause'));
  assert.ok(on(ws, 'master-main-gains-no-commit'), 'the change branch took the commits, not main');
  assert.ok(on(ws, 'product-repository-unchanged'));
  assert.ok(on(ws, 'product-code-untouched'));
  assert.ok(!matches(CASE, 'nothing-accepted', target(ws, CASE, 'nothing-accepted')), 'not_contains: the pattern must not match a draft');
  // file_exists is judged by the runner: pin the path the grader names against what the program wrote.
  assert.equal(read(ws, documentPath('plan')) !== '', true, 'plan-written looks where the plan lands');
});

test(`${CASE}: the document graders fail a draft that is not a light-track bug fix`, () => {
  const { intent, spec, plan } = fixDocuments(CHANGE, { track: null });
  assert.ok(!matches(CASE, 'intent-marks-the-light-track', intent), 'no track field');
  assert.ok(matches(CASE, 'intent-marks-the-light-track', fixDocuments().intent));
  assert.ok(!matches(CASE, 'spec-reproduces-the-bug', spec.replace('Welcome back, Mina', 'Hello')), 'a scenario that does not reproduce the report');
  assert.ok(!matches(CASE, 'spec-reproduces-the-bug', spec.replace('Welcome back, Mina', 'Welcome back,Mina')), 'the buggy text is not the expected text');
  assert.ok(!matches(CASE, 'plan-names-the-root-cause', plan.replaceAll('greet.sh', 'the greeting code')), 'no code named');
});

test(`${CASE}: an acceptance, a hand commit on main, and product code are what the failing run leaves`, () => {
  const ws = fixture(CASE);
  draftFix(ws);
  const doc = `.hoshin/copies/${CHANGE}/intent/demo/${CHANGE}/intent.md`;
  withHelpers(ws, `sed -i.bak 's/"status": "draft"/"status": "accepted"/' "${doc}" && rm "${doc}.bak" && commit_doc ${CHANGE} accept-intent intent/demo/${CHANGE}/intent.md`);
  assert.ok(matches(CASE, 'nothing-accepted', read(ws, copyLog())), 'an accept commit is seen');
  withHelpers(ws, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft intent x"');
  assert.ok(!on(ws, 'master-main-gains-no-commit'));
  withHelpers(ws, 'cd app && sed -i.bak \'s/,\\$2/, $2/\' greet.sh && rm greet.sh.bak && git_in commit -q -am "fix: space"');
  assert.ok(!on(ws, 'product-repository-unchanged'));
  assert.ok(!on(ws, 'product-code-untouched'));
});

test(`${CASE}: the reply grader needs the root cause at greet.sh line 4`, () => {
  for (const ok of ['Root cause: greet.sh:4 prints `,$2` with no space.', 'The bug is in greet.sh line 4.', 'See greet.sh, line 4.', 'greet.sh#L4', 'greet.sh (line 4)', 'On line 4 of greet.sh the echo drops the space.']) {
    assert.ok(matches(CASE, 'reply-gives-the-root-cause-line', ok), ok);
  }
  for (const no of ['The bug is in greet.sh.', 'Line 40 of greet.sh was changed.', 'The fix is drafted and waits for acceptance.', 'greet.sh:14']) {
    assert.ok(!matches(CASE, 'reply-gives-the-root-cause-line', no), no);
  }
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -m "docs: fix"')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C app log --oneline')));
});
