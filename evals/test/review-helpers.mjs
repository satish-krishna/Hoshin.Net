// Helpers the review cases' self-tests share (B12). Not a test file: node --test runs only "*.test.mjs".
//
// A review case starts from a built change and grades what the review leaves: build/review.md in the change's copy,
// the commit the hook makes of it in the copy's reflog, and the product repositories, which must stay as they were.
// These helpers write a review the way a passing (or failing) run would, and commit it through the program.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, graders, matches, skill, target, traceLine, withHelpers } from './lib.mjs';

/** The review document's path inside the workspace, which is what the cases' file graders read. */
export const reviewPath = (change) => `.hoshin/copies/${change}/intent/demo/${change}/build/review.md`;

const fence = (obj) => '```jsonc\n' + JSON.stringify(obj, null, 2) + '\n```';

/** One finding as the review document spells it: a jsonc block, then the text. */
export const finding = (id, fields, text = 'A finding.') => `### ${id}\n\n${fence({ id, ...fields })}\n\n${text}\n`;

/** A layout 2 review document with one section (or none, with an empty list of sections). */
export function reviewText({ commits = { app: 'abc1234' }, findings = [], sections = 1 } = {}) {
  let text = `${fence({ layout: 2 })}\n\n# Review findings\n`;
  for (let n = 1; n <= sections; n++) {
    text += `\n## Review ${n}\n\n${fence({ review: n, reviewed_at: '2026-11-26', commits })}\n`;
    if (n === sections) text += findings.map((f) => `\n${f}`).join('');
  }
  return text;
}

/** Writes build/review.md into the change's copy, as the model does before the hook commits it. */
export function writeReview(ws, change, text) {
  const path = join(ws, reviewPath(change));
  mkdirSync(join(path, '..'), { recursive: true });
  writeFileSync(path, text);
}

/** The commit the hook makes at the end of the turn, made here through the program (the review kind). */
export function commitReview(ws, change) {
  const out = withHelpers(ws, `cd "$(copy_of ${change})" && hoshin commit --json --kind review --change ${change} -- intent/demo/${change}/build/review.md`);
  assert.match(out, /"committed":true/, `the review was not committed: ${out}`);
}

/** The change's reflog in the master, which is where the copy's commits land. */
export const copyLog = (change) => `master/.git/worktrees/${change}/logs/HEAD`;

/** The case's real fixture, built on first use and shared by every test of the file, so the workspace is built once. */
export function lazyFixture(CASE) {
  let ws;
  return () => (ws ??= fixture(CASE));
}

/**
 * Registers the self-tests every review case shares, in an order that matters (the workspace is built once, and the
 * tests that commit come after the ones that read the state as built): the skill grader, the trace grader,
 * no-hand-commit, the review-commit and only-commit graders, the master's main and the product repositories.
 *
 * @param {string} CASE the case folder
 * @param {object} o
 * @param {string} o.change the change's name, as the case's prompt gives it
 * @param {string} o.review the text of a review a passing run writes (committed by the helper)
 * @param {() => string} [o.workspace] the file's lazyFixture, when its own tests must read the state as built first
 * @param {Record<string, string>} o.repos each product repository's reflog grader name -> the repository folder; the
 *   grader's pattern must pass as built and fail once a commit lands on top of the fixture's
 */
export function registerCommonTests(CASE, { change, review, repos, workspace }) {
  const built = workspace ?? lazyFixture(CASE);
  const log = copyLog(change);

  test(`${CASE}: the skill grader sees the review skill fire, plain or namespaced, and nothing else`, () => {
    assert.ok(matches(CASE, 'skill-fired', skill('hoshin:review')));
    assert.ok(matches(CASE, 'skill-fired', skill('review')));
    assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:build')));
    assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:fix')));
  });

  test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
    const dir = built();
    assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')), 'a fixture with no run must have no trace');
    mkdirSync(join(dir, 'master', '.git', 'hoshin'), { recursive: true });
    writeFileSync(join(dir, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
    assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')));
  });

  test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
    assert.ok(matches(CASE, 'no-hand-commit', bash(`git -C .hoshin/copies/${change} commit -m "docs: review"`)));
    assert.ok(matches(CASE, 'no-hand-commit', bash('cd app && git commit -am wip')));
    assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C app log --oneline')));
    assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C app diff main...HEAD')));
    assert.ok(!matches(CASE, 'no-hand-commit', bash('git status')));
  });

  test(`${CASE}: the product repositories and the master's main read as unchanged as built, and not once a commit lands on them`, () => {
    const dir = built();
    assert.ok(matches(CASE, 'master-main-gains-no-commit', target(dir, CASE, 'master-main-gains-no-commit')), 'master main');
    for (const grader of Object.keys(repos)) {
      assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on the fixture as built`);
    }
  });

  test(`${CASE}: the review commit is the library's, made on the change's branch and nowhere else`, () => {
    const dir = built();
    assert.ok(!matches(CASE, 'review-commit-landed', target(dir, CASE, 'review-commit-landed')), 'a fixture with no review must not read as reviewed');
    // The commits the run's own turn would not make: a draft of another change, and a review of another change.
    assert.ok(!matches(CASE, 'review-commit-landed', `x\tcommit: docs(demo): draft intent ${change}\n`));
    assert.ok(!matches(CASE, 'review-commit-landed', `x\tcommit: docs(demo): record review 1 of 2026-11-01-another-change\n`));
    assert.ok(matches(CASE, 'review-commit-landed', `x\tcommit: docs(demo): review ${change}\n`), 'the hook subject');
    assert.ok(matches(CASE, 'review-commit-landed', `x\tcommit: docs(demo): record review 1 of ${change}\n`), 'the review kind subject');
    assert.ok(!matches(CASE, 'review-commit-landed', `x\tcommit: docs(demo): record review 2 of ${change}\n`), 'a second review is not the first');
    // A case that starts from an uncommitted review passes review: null, and the draft is committed as it is.
    if (review !== null) writeReview(dir, change, review);
    commitReview(dir, change);
    assert.ok(matches(CASE, 'review-commit-landed', target(dir, CASE, 'review-commit-landed')));
    const only = 'review-is-the-only-commit-after-the-log';
    if (only in graders(CASE)) {
      assert.ok(!matches(CASE, only, target(dir, CASE, only)), 'the review is the only commit after the log');
      withHelpers(dir, `git_in -C "$(copy_of ${change})" commit -q --allow-empty -m "docs(demo): stray commit"`);
      assert.ok(matches(CASE, only, target(dir, CASE, only)), 'a stray commit after the log is seen');
    }
  });

  test(`${CASE}: a commit on the master's main or on a product repository is seen`, () => {
    const dir = built();
    withHelpers(dir, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): draft intent x"');
    assert.ok(!matches(CASE, 'master-main-gains-no-commit', target(dir, CASE, 'master-main-gains-no-commit')));
    for (const [grader, folder] of Object.entries(repos)) {
      withHelpers(dir, `cd ${folder} && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "feat(demo): stray"`);
      assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} still passes after a commit on ${folder}`);
    }
  });

  return { workspace: built };
}

