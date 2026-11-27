// Self-test for setup-refreshes-instructions: each grader is tried on what a passing run leaves and on what a failing
// run leaves. A passing run is made through the program: the instructions command, then the instructions commit.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, read, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'setup-refreshes-instructions';
let ws;
const built = () => (ws ??= fixture(CASE));

test(`${CASE}: the skill grader sees setup fire, plain or namespaced, and nothing else`, () => {
  assert.ok(matches(CASE, 'skill-fired', skill('hoshin:setup')));
  assert.ok(matches(CASE, 'skill-fired', skill('setup')));
  assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:context')));
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const dir = built();
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')), 'a fixture with no run must have no trace');
  mkdirSync(join(dir, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(dir, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')));
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -m "chore(demo): refresh workspace instructions"')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
});

test(`${CASE}: as built, both files hold the old instructions, the notes are around the block, and nothing was refreshed`, () => {
  const dir = built();
  for (const grader of ['old-block-replaced', 'old-instructions-replaced']) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} does not see the old text in the fixture`);
  }
  for (const grader of ['notes-before-kept', 'notes-after-kept', 'marker-untouched']) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on the fixture as built`);
  }
  for (const grader of ['block-holds-a-copy', 'instructions-were-committed-as-a-refresh', 'instructions-list-the-system']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} passes before the run`);
  }
  for (const grader of ['no-other-commit-landed', 'not-two-refreshes', 'no-full-setup-commit']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} fires on the fixture as built`);
  }
});

test(`${CASE}: what the program leaves when the instructions are refreshed and committed satisfies every grader`, () => {
  const dir = built();
  withHelpers(dir, 'cd master && hoshin instructions --json --write');
  const out = withHelpers(dir, 'cd master && hoshin commit --json --kind instructions -- intent/workspace.md');
  assert.match(out, /"committed":true/, out);
  for (const grader of [
    'instructions-list-the-system',
    'block-holds-a-copy',
    'notes-before-kept',
    'notes-after-kept',
    'instructions-were-committed-as-a-refresh',
    'marker-untouched',
  ]) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on a passing run`);
  }
  for (const grader of ['old-block-replaced', 'old-instructions-replaced', 'block-appears-once', 'not-two-refreshes', 'no-full-setup-commit', 'no-other-commit-landed']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} fires on a passing run`);
  }
  assert.ok(read(dir, 'CLAUDE.md').includes(read(dir, 'master/intent/workspace.md').trim()), 'the block holds a full copy of intent/workspace.md');
});

test(`${CASE}: a full setup commit, a second refresh, or another commit is seen`, () => {
  const dir = built();
  withHelpers(dir, 'cd master && echo "" >> intent/workspace.md');
  assert.match(withHelpers(dir, 'cd master && hoshin commit --json --kind instructions -- intent/workspace.md'), /"committed":true/);
  assert.ok(matches(CASE, 'not-two-refreshes', target(dir, CASE, 'not-two-refreshes')), 'two refreshes');
  withHelpers(dir, 'cd master && echo "" >> intent/workspace.md');
  assert.match(withHelpers(dir, 'cd master && hoshin commit --json --kind setup --system demo -- intent/workspace.md'), /"committed":true/);
  assert.ok(matches(CASE, 'no-full-setup-commit', target(dir, CASE, 'no-full-setup-commit')), 'a refresh committed as a full setup');
  assert.ok(!matches(CASE, 'instructions-were-committed-as-a-refresh', target(dir, CASE, 'instructions-were-committed-as-a-refresh')), 'the refresh is no longer the last commit');
  withHelpers(dir, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): other"');
  assert.ok(matches(CASE, 'no-other-commit-landed', target(dir, CASE, 'no-other-commit-landed')), 'a commit with another subject');
});

test(`${CASE}: the CLAUDE.md graders fail on what a failing run writes`, () => {
  const block = '<!-- hoshin:start -->\n<!-- generated by hoshin setup from master/intent/workspace.md; edit outside this block -->\n# hoshin workspace\n\n| demo | app |\n<!-- hoshin:end -->';
  const good = `# My notes\n\nKeep this line exactly as it is.\n\n${block}\n\n## After\n\nKeep this line too.\n`;
  assert.ok(matches(CASE, 'notes-before-kept', good) && matches(CASE, 'notes-after-kept', good) && matches(CASE, 'block-holds-a-copy', good));
  assert.ok(!matches(CASE, 'old-block-replaced', good));
  // The old block left in place.
  assert.ok(matches(CASE, 'old-block-replaced', good.replace('# hoshin workspace', 'OLD INSTRUCTIONS FOR A SYSTEM THAT NO LONGER EXISTS')));
  // The whole file regenerated, losing the engineer's text on one side or both.
  assert.ok(!matches(CASE, 'notes-before-kept', `${block}\n\n## After\n\nKeep this line too.\n`), 'the notes before were dropped');
  assert.ok(!matches(CASE, 'notes-after-kept', `# My notes\n\nKeep this line exactly as it is.\n\n${block}\n`), 'the section after was dropped');
  assert.ok(!matches(CASE, 'notes-after-kept', good.replace('Keep this line too.', 'Changed.')), 'the section after was reworded');
  assert.ok(!matches(CASE, 'notes-before-kept', good.replace('Keep this line exactly as it is.', 'Changed.')), 'the notes were reworded');
  // The block appended again instead of replaced.
  assert.ok(matches(CASE, 'block-appears-once', good + '\n' + block), 'a second block');
  assert.ok(!matches(CASE, 'block-holds-a-copy', good.replace('| demo | app |', '| shop | web |')), 'a block for another system');
  assert.ok(!matches(CASE, 'marker-untouched', '{ "version": 1, "systems": { "demo": { "repos": ["app"] } }, "hooks": {} }'), 'the marker was rewritten');
});
