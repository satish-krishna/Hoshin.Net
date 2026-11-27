// Helpers shared by the self-tests of the light-track fix cases (group "fix"). Not a test file: node --test runs only
// *.test.mjs. The functions of evals/shared/fix.sh are loaded the way withHelpers loads the workspace's, so a self-test
// brings a change to the state a passing or failing run leaves through the program, never by writing history by hand.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, evals, fixture, matches, read, skill, target, traceLine, withHelpers } from './lib.mjs';

const fixSh = join(evals, 'shared', 'fix.sh').replaceAll('\\', '/');

/** Runs bash in the workspace with the workspace helpers and fix.sh's functions loaded. */
export const withFix = (ws, script) => withHelpers(ws, `eval "$(sed -n '/^fix_change=/,$p' "${fixSh}")"\n${script}`);

/** Rewrites one file of the workspace through a function of its text; the new text must differ. */
export function edit(ws, rel, change) {
  const before = read(ws, rel);
  const after = change(before);
  assert.notEqual(after, before, `${rel} was not changed by the edit`);
  writeFileSync(join(ws, rel), after);
}

/** A change's folder in its copy, and its reflog, as the case files name them. */
export const folderOf = (change) => `.hoshin/copies/${change}/intent/demo/${change}`;
export const reflogOf = (change) => `master/.git/worktrees/${change}/logs/HEAD`;

/**
 * The three graders every fix case shares, each tried on both sides: skill-fired (the skills it accepts, and one it
 * must not), hook-trace-has-an-entry (absent on the fixture as built, present once a hook has written it), and, when
 * the case has it, no-hand-commit (a typed git commit fires it, other git commands don't).
 */
export function commonTests(CASE, { fires, notFires = 'hoshin:intent', handCommit = true }) {
  test(`${CASE}: the skill grader sees ${fires.join(' or ')} fired, plain or namespaced, and not ${notFires}`, () => {
    for (const name of fires) {
      assert.ok(matches(CASE, 'skill-fired', skill(name)), name);
      assert.ok(matches(CASE, 'skill-fired', skill(`hoshin:${name}`)), `hoshin:${name}`);
    }
    assert.ok(!matches(CASE, 'skill-fired', skill(notFires)));
    assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:accept')));
  });

  test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
    const ws = fixture(CASE);
    assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')), 'a fixture with no run must have no trace');
    mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
    writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
    assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
  });

  if (handCommit) {
    test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
      assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -m "docs: draft fix"')));
      assert.ok(matches(CASE, 'no-hand-commit', bash('cd .hoshin/copies/x && git commit -am wip')));
      assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
      assert.ok(!matches(CASE, 'no-hand-commit', bash('git status && git diff')));
    });
  }
}

/** The text of the one-repository refusal and of the intent recommendation, right and near-miss, for 14 and 15. */
export const boundaryReplies = {
  good: 'This feedback needs a second repository, web, and a light-track fix stays in one repository (rule 1). I changed nothing. Run /hoshin:intent to take the bold greeting through the full pipeline.',
  intentSkill: 'A fix may touch only one repository, and this touches two. Use the intent skill for the web change.',
  noRule: 'I will not do this one as a quick fix. Please start with the intent skill.',
  noIntent: 'A light-track fix stays in one repository, and the web repository is a second repository, so I stopped.',
};
