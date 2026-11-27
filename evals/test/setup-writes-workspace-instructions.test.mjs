// Self-test for setup-writes-workspace-instructions: each grader is tried on what a passing run leaves and on what a
// failing run leaves. A passing run is made through the program (the instructions command, then the setup commit),
// which is what the library does at the end of the hook-issued setup step.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, read, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'setup-writes-workspace-instructions';
let ws;
const built = () => (ws ??= fixture(CASE));

const writeInstructionsAndCommit = (dir) => {
  withHelpers(dir, 'cd master && hoshin instructions --json --write');
  const out = withHelpers(dir, 'cd master && hoshin commit --json --kind setup --system demo -- intent/workspace.md');
  assert.match(out, /"committed":true/, out);
};

test(`${CASE}: the skill grader sees setup fire, plain or namespaced, and nothing else`, () => {
  assert.ok(matches(CASE, 'skill-fired', skill('hoshin:setup')));
  assert.ok(matches(CASE, 'skill-fired', skill('setup')));
  assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:context')));
  assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:intent')));
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const dir = built();
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')), 'a fixture with no run must have no trace');
  mkdirSync(join(dir, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(dir, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')));
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -m "chore(demo): set up hoshin for demo"')));
  assert.ok(matches(CASE, 'no-hand-commit', bash('cd master && git commit -am wip')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master status --short')));
});

test(`${CASE}: as built, no instructions exist yet and the engineer's notes are all that is in CLAUDE.md`, () => {
  const dir = built();
  for (const grader of ['instructions-list-the-system', 'instructions-point-at-context', 'setup-commit-landed', 'block-holds-a-copy']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} passes before the run`);
  }
  for (const grader of ['engineer-notes-kept', 'marker-keeps-the-repositories']) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on the fixture as built`);
  }
  for (const grader of ['block-appears-once', 'not-two-setup-commits', 'no-other-commit-landed']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} fires on the fixture as built`);
  }
  assert.equal(read(dir, 'master/intent/workspace.md'), '', 'there is no intent/workspace.md yet');
});

test(`${CASE}: what the program leaves when the instructions are written and committed satisfies every file and reflog grader`, () => {
  const dir = built();
  writeInstructionsAndCommit(dir);
  for (const grader of [
    'instructions-list-the-system',
    'instructions-point-at-context',
    'setup-commit-landed',
    'marker-keeps-the-repositories',
    'block-holds-a-copy',
    'engineer-notes-kept',
  ]) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on a passing run`);
  }
  for (const grader of ['instructions-have-no-placeholders-left', 'not-two-setup-commits', 'no-other-commit-landed', 'block-appears-once']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} fires on a passing run`);
  }
  const block = read(dir, 'CLAUDE.md');
  assert.ok(block.includes(read(dir, 'master/intent/workspace.md').trim()), 'the block holds a full copy of intent/workspace.md');
});

test(`${CASE}: a second setup commit, another subject, or a duplicated block is seen`, () => {
  const dir = built();
  // A second setup commit: a changed instructions file committed again as a setup.
  withHelpers(dir, 'cd master && echo "" >> intent/workspace.md');
  const again = withHelpers(dir, 'cd master && hoshin commit --json --kind setup --system demo -- intent/workspace.md');
  assert.match(again, /"committed":true/);
  assert.ok(matches(CASE, 'not-two-setup-commits', target(dir, CASE, 'not-two-setup-commits')), 'two setup commits');
  withHelpers(dir, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): write instructions"');
  assert.ok(matches(CASE, 'no-other-commit-landed', target(dir, CASE, 'no-other-commit-landed')), 'a commit with another subject');
  const block = read(dir, 'CLAUDE.md');
  const twice = block + '\n' + block.slice(block.indexOf('<!-- hoshin:start -->'));
  assert.ok(matches(CASE, 'block-appears-once', twice), 'the block twice');
});

test(`${CASE}: the instruction and CLAUDE.md graders fail on what a failing run writes`, () => {
  const notes = '# My notes\n\nKeep this line exactly as it is.\n';
  const block = '<!-- hoshin:start -->\n<!-- generated by hoshin setup from master/intent/workspace.md; edit outside this block -->\n# hoshin workspace\n\n| System | Repositories |\n|---|---|\n| demo | app |\n<!-- hoshin:end -->\n';
  assert.ok(matches(CASE, 'block-holds-a-copy', notes + '\n' + block));
  assert.ok(!matches(CASE, 'block-holds-a-copy', notes), 'no block');
  assert.ok(!matches(CASE, 'block-holds-a-copy', block.replace('| demo | app |', '| shop | web |')), 'a block for another system');
  assert.ok(!matches(CASE, 'block-holds-a-copy', block.replace('<!-- hoshin:end -->', '')), 'no end marker');
  assert.ok(!matches(CASE, 'block-holds-a-copy', block.replace('# hoshin workspace', '# Workspace')), 'not the generated text');
  assert.ok(!matches(CASE, 'engineer-notes-kept', block), 'the notes were replaced by the block');
  assert.ok(!matches(CASE, 'engineer-notes-kept', '# Notes\n\nKeep this line exactly as it is.\n' + block), 'the heading was changed');
  assert.ok(matches(CASE, 'engineer-notes-kept', block.replace(/^/, notes + '\n')));
  assert.ok(!matches(CASE, 'instructions-list-the-system', '| System | Repositories |\n|---|---|\n| shop | web |\n'));
  assert.ok(matches(CASE, 'instructions-have-no-placeholders-left', '| <system> | <repo>, <repo> |'), 'the template row was left in');
  assert.ok(matches(CASE, 'instructions-have-no-placeholders-left', 'see <master>/intent/'), 'a <master> placeholder');
  assert.ok(!matches(CASE, 'instructions-point-at-context', 'master/intent/other/context/'));
  assert.ok(!matches(CASE, 'marker-keeps-the-repositories', '{ "version": 1, "systems": { "demo": { "repos": ["app", "web"] } } }'), 'a repository was added');
  assert.ok(!matches(CASE, 'marker-keeps-the-repositories', '{ "version": 1, "systems": { "demo": { "repos": [] } } }'), 'a repository was removed');
});

test(`${CASE}: the report grader wants the next step, the intent skill, and not a report that stops short`, () => {
  assert.ok(matches(CASE, 'report-gives-the-next-step', 'Done. A product owner can now run /hoshin:intent for demo.'));
  assert.ok(matches(CASE, 'report-gives-the-next-step', 'Next step: the intent skill.'));
  assert.ok(!matches(CASE, 'report-gives-the-next-step', 'Wrote the workspace instructions and committed them.'));
});
