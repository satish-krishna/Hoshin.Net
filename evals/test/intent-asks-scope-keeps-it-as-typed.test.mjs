// Self-test for intent-asks-scope-keeps-it-as-typed: the scope is typed over an offered default during the interview, and
// must end up as typed in the front matter and in the draft commit's header.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { caseText, evals } from './lib.mjs';
import { scopeTests } from './intent-scope-common.mjs';

const CASE = 'intent-asks-scope-keeps-it-as-typed';

scopeTests(CASE);

test(`${CASE}: the invocation carries no scope, and the product owner is told to type one`, () => {
  const text = caseText(CASE);
  assert.doesNotMatch(text, /scope:PROJ-123/, 'no scope argument in the invocation');
  assert.match(text, /type the scope PROJ-123/);
  assert.match(readFileSync(join(evals, 'intent-keeps-scope-as-typed', 'case.yaml'), 'utf8'), /scope:PROJ-123/, 'the sibling case passes it in the invocation');
});
