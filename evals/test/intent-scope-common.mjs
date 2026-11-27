// The self-tests the two scope cases share (intent-asks-scope-keeps-it-as-typed and intent-keeps-scope-as-typed): they grade
// the same artifacts, and differ only in how the scope reaches the model. Not a test file itself.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fixture, matches, read, target, withHelpers } from './lib.mjs';
import { commonTests } from './plan-common.mjs';

const CHANGE = '2026-11-01-password-reset';
const intentRel = `intent/demo/${CHANGE}/intent.md`;

const intentText = (scope) => `\`\`\`jsonc
{ "title": "Customer can reset password", "system": "demo", "author": "Pat Owner, Product Owner", "date": "2026-11-01", "status": "draft", "scope": "${scope}" }
\`\`\`

# Intent: Customer can reset password

## Problem

A customer who forgot the password can only phone support.

## Proposed outcome

A customer resets the password from the sign-in page with a one-time link.

## Affected users and systems

- **Users:** customers and support staff.

## Constraints

- The link expires within an hour.

## Decisions

| # | Situation | Decision | What should happen |
|---|---|---|---|
| D1 | The link is used twice | handle it | The second use is refused. |

## Open questions

- None.
`;

/** Starts the change and commits a draft intent with the given scope through the program, as the Stop hook does. */
function draft(ws, scope) {
  withHelpers(ws, `change_start ${CHANGE} feat demo`);
  writeFileSync(join(ws, '.hoshin', 'copies', CHANGE, ...intentRel.split('/')), intentText(scope));
  withHelpers(ws, `commit_doc ${CHANGE} intent-draft ${intentRel}`);
}

export function scopeTests(CASE, { asksNothing = false } = {}) {
  const intentFile = (ws) => read(ws, `.hoshin/copies/${CHANGE}/${intentRel}`);
  const reflog = (ws) => read(ws, `master/.git/worktrees/${CHANGE}/logs/HEAD`);

  commonTests(CASE, 'intent');

  test(`${CASE}: a scope kept as typed passes every grader, and the draft commit's header keeps the capitals`, () => {
    const ws = fixture(CASE);
    assert.ok(!matches(CASE, 'draft-commit-keeps-the-capitals', reflog(ws)), 'nothing drafted yet');
    draft(ws, 'PROJ-123');
    assert.ok(matches(CASE, 'front-matter-keeps-the-scope-as-typed', intentFile(ws)));
    assert.ok(matches(CASE, 'draft-commit-keeps-the-capitals', reflog(ws)), reflog(ws));
    // The two not_contains graders: a match is the failure.
    assert.ok(!matches(CASE, 'scope-never-lowercased', intentFile(ws)));
    assert.ok(!matches(CASE, 'no-lowercased-commit', reflog(ws)));
    assert.ok(matches(CASE, 'scope-never-lowercased', `${intentFile(ws)}\nSee proj-123.`), 'a lowercase mention in the body is seen');
  });

  test(`${CASE}: the system's scope or a lowercased scope fails`, () => {
    const system = fixture(CASE);
    draft(system, 'demo');
    assert.ok(!matches(CASE, 'front-matter-keeps-the-scope-as-typed', intentFile(system)), 'the system scope');
    assert.ok(!matches(CASE, 'draft-commit-keeps-the-capitals', reflog(system)), 'the system scope in the header');
    assert.ok(!matches(CASE, 'front-matter-keeps-the-scope-as-typed', intentText('proj-123')), 'lowercased front matter');
    assert.ok(matches(CASE, 'scope-never-lowercased', intentText('proj-123')), 'lowercased front matter is seen');
    // A lowercased header, as a hand-written commit would leave it.
    assert.ok(matches(CASE, 'no-lowercased-commit', 'a b eval <e> 1 +0000\tcommit: docs(proj-123): draft intent 2026-11-01-password-reset\n'));
    assert.ok(!matches(CASE, 'draft-commit-keeps-the-capitals', 'a b eval <e> 1 +0000\tcommit: docs(proj-123): draft intent 2026-11-01-password-reset\n'));
  });

  test(`${CASE}: the front matter is read as JSONC, quoted, and not from a YAML line`, () => {
    assert.ok(!matches(CASE, 'front-matter-keeps-the-scope-as-typed', 'scope: PROJ-123'), 'a YAML line is not Hoshin.Net front matter');
    assert.ok(matches(CASE, 'front-matter-keeps-the-scope-as-typed', '{ "scope" : "PROJ-123" }'));
  });

  if (asksNothing) {
    test(`${CASE}: a reply that asks for the scope is seen (not_contains: a match is the failure), and one that reports it is not`, () => {
      assert.ok(matches(CASE, 'scope-not-asked', 'Which commit scope should I use for this change?'));
      assert.ok(matches(CASE, 'scope-not-asked', 'What scope do you want?'));
      assert.ok(!matches(CASE, 'scope-not-asked', 'The intent is drafted and committed under the scope PROJ-123 you gave.'));
    });
  }
}
