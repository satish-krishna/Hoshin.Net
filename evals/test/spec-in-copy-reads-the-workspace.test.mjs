// Self-test for spec-in-copy-reads-the-workspace: each grader is tried on what a passing run leaves and on what a failing run leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, realpathSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, program, read, sh, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'spec-in-copy-reads-the-workspace';
const CHANGE = '2026-11-01-new-arrivals';
const on = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));
const cli = (ws, args) => JSON.parse(sh(ws, `dotnet "${program.replace(/\\/g, '/')}" ${args}`));
const same = (a, b) => realpathSync.native(a).toLowerCase() === realpathSync.native(b).toLowerCase();

const fence = (obj) => '```jsonc\n' + JSON.stringify(obj, null, 2) + '\n```';
const specText = (design) =>
  `${fence({ layout: 2, source_intent: `${CHANGE}/intent.md`, source_intent_commit: 'pending acceptance', system: 'demo', date: '2026-11-01', status: 'draft', policies_applied: [], policies_not_checked: ['security'] })}\n\n` +
  '# Spec: Show new arrivals to returning visitors\n\n## Requirements\n\n```gherkin\nFeature: Show new arrivals to returning visitors\n  Returning visitors see a New arrivals note.\n\n  @R1 @from:outcome-1\n  Rule: A returning visitor sees the note\n\n    @R1.1\n    Scenario: A returning visitor opens the home page\n      Given a returning visitor named "Mina"\n      When the home page opens\n      Then the New arrivals note shows below the greeting\n\n  @R2 @from:outcome-2\n  Rule: A first-time visitor does not see the note\n\n    @R2.1\n    Scenario: A first-time visitor opens the home page\n      Given a first-time visitor\n      When the home page opens\n      Then no New arrivals note shows\n```\n\n' +
  `## Design\n\n${design}\n`;
const writeSpec = (ws, design = 'The note is printed by home.sh, after the greeting.') => {
  const folder = join(ws, 'intent', 'demo', CHANGE);
  mkdirSync(folder, { recursive: true });
  writeFileSync(join(folder, 'spec.md'), specText(design));
};
// What the hook's Stop does for a spec: commits it through the program, from the session's folder, which is the copy.
const commitSpec = (ws) => withHelpers(ws, `hoshin commit --json --kind spec --change ${CHANGE} -- intent/demo/${CHANGE}/spec.md | grep -q '"committed":true'`);

test(`${CASE}: the skill grader sees spec, plain or namespaced`, () => {
  assert.ok(matches(CASE, 'skill-fired', skill('hoshin:spec', CHANGE)));
  assert.ok(matches(CASE, 'skill-fired', skill('spec')));
  assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:plan')));
});

test(`${CASE}: the fixture's session folder is the change's copy, with the workspace around it, and the context one commit behind app`, () => {
  const ws = fixture(CASE);
  const resolved = cli(ws, 'resolve --json');
  assert.equal(resolved.masterIsCopy, true, 'no options inside the copy: the master is the copy');
  assert.ok(same(resolved.master, ws), 'the copy is the case folder');
  assert.ok(same(resolved.workspace, ws), 'the workspace is found from the copy, and holds master/ and app/');
  assert.deepEqual(resolved.problems, []);
  assert.equal(sh(ws, 'git status --porcelain').trim(), '', 'the copy is clean: master/ and app/ are excluded');
  assert.equal(sh(ws, 'git branch --show-current').trim(), `change/${CHANGE}`, 'the copy is on the change branch');
  assert.equal(sh(ws, 'git -C master branch --show-current').trim(), 'main', 'the engineer\'s checkout is on main');
  const stale = cli(ws, 'stale --json --system demo').repos[0];
  assert.deepEqual([stale.stale, stale.behind, stale.reason], [true, 1, 'behind'], 'app has one commit after the context file');
  assert.match(read(ws, `intent/demo/${CHANGE}/intent.md`), /"status": "accepted"/, 'the intent is accepted');
  assert.match(sh(ws, 'git log --format=%s'), /accept intent/, 'accepted through the program');
  assert.ok(on(ws, 'engineer-checkout-still-on-main'));
  assert.ok(!matches(CASE, 'engineer-checkout-gained-no-commit', read(ws, 'master/.git/logs/HEAD')), 'not_contains holds on the fixture');
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const ws = fixture(CASE);
  assert.ok(!on(ws, 'hook-trace-has-an-entry'));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(on(ws, 'hook-trace-has-an-entry'));
});

test(`${CASE}: a spec committed in the copy passes the file and reflog graders, and a spec that never read the code does not`, () => {
  const ws = fixture(CASE);
  assert.ok(!on(ws, 'commit-landed-in-the-copy'), 'nothing committed yet');
  assert.ok(!on(ws, 'spec-uses-the-product-code'), 'no spec yet');
  writeSpec(ws);
  commitSpec(ws);
  assert.ok(on(ws, 'spec-is-a-draft'));
  assert.ok(on(ws, 'spec-uses-the-product-code'));
  assert.ok(on(ws, 'commit-landed-in-the-copy'), 'the commit is in the copy\'s reflog');
  assert.ok(!matches(CASE, 'engineer-checkout-gained-no-commit', read(ws, 'master/.git/logs/HEAD')), 'a copy commit is not the engineer\'s');
  assert.ok(on(ws, 'engineer-checkout-still-on-main'));
  assert.ok(!matches(CASE, 'spec-uses-the-product-code', specText('The note is shown on the home page.')), 'a design that names no code');
  assert.ok(!matches(CASE, 'spec-is-a-draft', specText('x').replace('"draft"', '"accepted"')), 'an accepted spec');
});

test(`${CASE}: a commit in the engineer's checkout, or a switched branch there, is what the failing run leaves`, () => {
  const ws = fixture(CASE);
  withHelpers(ws, `cd master && mkdir -p intent/demo/${CHANGE} && echo spec > intent/demo/${CHANGE}/spec.md && git_in add . && git_in commit -q -m "docs(demo): write spec ${CHANGE}"`);
  assert.ok(matches(CASE, 'engineer-checkout-gained-no-commit', read(ws, 'master/.git/logs/HEAD')), 'a commit in the engineer\'s checkout is seen');
  assert.ok(!on(ws, 'commit-landed-in-the-copy'), 'and is not in the copy');
  withHelpers(ws, 'git_in -C master checkout -q -b other');
  assert.ok(!on(ws, 'engineer-checkout-still-on-main'), 'a switched branch is seen');
});

test(`${CASE}: the stale-context grader needs the one commit and the staleness in one sentence`, () => {
  const said = (text) => matches(CASE, 'reports-the-stale-context', text);
  assert.ok(said('The context file for app is stale: 1 commit behind.'));
  assert.ok(said("app's context is one commit behind its source_commit."));
  assert.ok(said('Note: the app context is out of date (1 commit since it was built).'));
  assert.ok(said('The context is stale by one commit, so I read app as it is now.'));
  assert.ok(!said('The spec is committed.'), 'no mention');
  assert.ok(!said('The context file is current.'));
  assert.ok(!said('The context is stale.'), 'stale, but not by how much');
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(matches(CASE, 'no-hand-commit', bash('git commit -m "docs(demo): write spec"')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C app log --oneline')));
});
