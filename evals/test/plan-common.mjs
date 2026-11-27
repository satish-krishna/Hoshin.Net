// The three tests every plan case's self-test shares: its skill, trace and no-hand-commit graders. Not a test file itself.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, skill, target, traceLine } from './lib.mjs';

export function commonTests(CASE, skillName = 'plan') {
  test(`${CASE}: the skill grader sees the ${skillName} skill, plain or namespaced, and nothing else`, () => {
    assert.ok(matches(CASE, 'skill-fired', skill(`hoshin:${skillName}`)));
    assert.ok(matches(CASE, 'skill-fired', skill(skillName)));
    assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:spec')));
  });

  test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
    const ws = fixture(CASE);
    assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')), 'a fixture with no run must have no trace');
    mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
    writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
    assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(ws, CASE, 'hook-trace-has-an-entry')));
  });

  test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
    assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -m "docs: x"')));
    assert.ok(matches(CASE, 'no-hand-commit', bash('cd master && git commit -am wip')));
    assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
  });
}
