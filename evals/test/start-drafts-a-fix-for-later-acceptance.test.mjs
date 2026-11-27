// Self-test for start-drafts-a-fix-for-later-acceptance: each grader is tried on what a passing run leaves and on what a failing run leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, read, skill, target, traceLine, withHelpers, writeCall } from './lib.mjs';
import { CHANGE, copyLog, draftFix, redraftFix } from './start-docs.mjs';

const CASE = 'start-drafts-a-fix-for-later-acceptance';
const on = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));

test(`${CASE}: the skill graders see start and fix, plain or namespaced, and not one for the other`, () => {
  assert.ok(matches(CASE, 'start-fired', skill('hoshin:start')));
  assert.ok(matches(CASE, 'start-fired', skill('start')));
  assert.ok(!matches(CASE, 'start-fired', skill('hoshin:fix')));
  assert.ok(matches(CASE, 'fix-fired', skill('hoshin:fix', CHANGE)));
  assert.ok(!matches(CASE, 'fix-fired', skill('hoshin:start')));
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const ws = fixture(CASE);
  assert.ok(!on(ws, 'hook-trace-has-an-entry'));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(on(ws, 'hook-trace-has-an-entry'));
});

test(`${CASE}: one draft commit passes, no draft or two drafts fail, and an acceptance is seen`, () => {
  const ws = fixture(CASE);
  assert.ok(!on(ws, 'drafted-once'), 'the fixture has no change, so no draft');
  assert.ok(!matches(CASE, 'accepted-nothing', read(ws, copyLog())), 'nothing accepted in the fixture');
  draftFix(ws);
  assert.ok(on(ws, 'drafted-once'), 'one draft commit, after the start commit');
  assert.ok(!matches(CASE, 'accepted-nothing', read(ws, copyLog())), 'a draft is not an acceptance');
  redraftFix(ws);
  assert.ok(!on(ws, 'drafted-once'), 'two draft commits');
  const doc = `.hoshin/copies/${CHANGE}/intent/demo/${CHANGE}/intent.md`;
  withHelpers(ws, `sed -i.bak 's/"status": "draft"/"status": "accepted"/' "${doc}" && rm "${doc}.bak" && commit_doc ${CHANGE} accept-intent intent/demo/${CHANGE}/intent.md`);
  assert.ok(matches(CASE, 'accepted-nothing', read(ws, copyLog())), 'an acceptance commit is seen');
});

test(`${CASE}: a document written as accepted is seen in a Write, an Edit or a Bash call, and a draft or a plain read is not`, () => {
  const accepted = '```jsonc\n{ "title": "Fix", "status": "accepted", "scope": "demo" }\n```\n';
  const draft = '```jsonc\n{ "title": "Fix", "status": "draft", "scope": "demo" }\n```\n';
  for (const grader of ['no-document-written-as-accepted-by-write', 'no-document-written-as-accepted-by-edit', 'no-document-written-as-accepted-by-bash']) {
    assert.ok(matches(CASE, grader, writeCall('intent.md', accepted)), `${grader}: an accepted document is seen`);
    assert.ok(matches(CASE, grader, JSON.stringify({ file_path: 'intent.md', old_string: '"status": "draft"', new_string: '"status": "accepted"' })), `${grader}: a status edit is seen`);
    assert.ok(matches(CASE, grader, bash(`cat > intent.md <<'EOF'\n${accepted}EOF`)), `${grader}: a heredoc is seen`);
    assert.ok(!matches(CASE, grader, writeCall('intent.md', draft)), `${grader}: a draft`);
    assert.ok(!matches(CASE, grader, bash('git -C master status --short')), `${grader}: an unrelated command`);
  }
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -m "docs(demo): draft fix"')));
  assert.ok(matches(CASE, 'no-hand-commit', bash('cd master && git commit -am wip')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
});

test(`${CASE}: the prompt hands the whole job to the session and offers the light track`, () => {
  const prompt = read(join(import.meta.dirname, '..', CASE), 'case.yaml');
  assert.match(prompt, /run the fix skill in this session/);
  assert.match(prompt, /If you offer the choices, I take the light track/);
  assert.doesNotMatch(prompt, /accept(?:ed)? it in advance/i, 'no advance acceptance is offered');
});
