// Self-test for runner-typed-command-is-the-users, the probe that answers README's "Typed commands" (B02 item 3).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { matches, traceLine } from './lib.mjs';

const CASE = 'runner-typed-command-is-the-users';

test(`${CASE}: typed-as-user passes on a typed slash command's trace entry and not on a model's Skill call`, () => {
  assert.ok(matches(CASE, 'typed-as-user', traceLine('UserPromptExpansion')));
  assert.ok(!matches(CASE, 'typed-as-user', traceLine('PostToolUse')));
  assert.ok(!matches(CASE, 'typed-as-user', traceLine('UserPromptSubmit')));
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', traceLine('PostToolUse')));
});
