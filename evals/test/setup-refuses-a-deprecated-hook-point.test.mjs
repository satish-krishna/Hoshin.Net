// Self-test for setup-refuses-a-deprecated-hook-point: each grader is tried on what a passing run leaves (nothing
// written, nothing committed, a report that refuses and names the points that stay) and on what a failing run leaves (a
// hook on task.done, written to either file or as a script, committed or not).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, read, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'setup-refuses-a-deprecated-hook-point';
let ws;
const built = () => (ws ??= fixture(CASE));

const good =
  'Hooks on task.done are deprecated, so I added nothing. The events that keep their hooks are build.start, build.finish and change.done; tell me which one you want and I will add it.';

test(`${CASE}: the skill grader sees setup fire, plain or namespaced, and nothing else`, () => {
  assert.ok(matches(CASE, 'skill-fired', skill('hoshin:setup')));
  assert.ok(matches(CASE, 'skill-fired', skill('setup')));
  assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:build')));
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const dir = built();
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')), 'a fixture with no run must have no trace');
  mkdirSync(join(dir, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(dir, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')));
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -am "chore(demo): add a hook"')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
});

test(`${CASE}: the report graders want the refusal and all three events that keep their hooks`, () => {
  assert.ok(matches(CASE, 'says-deprecated', good));
  assert.ok(matches(CASE, 'says-deprecated', 'That event is Deprecated.'));
  assert.ok(!matches(CASE, 'says-deprecated', 'I could not add the hook.'));
  assert.ok(matches(CASE, 'names-the-three-points-that-stay', good));
  assert.ok(matches(CASE, 'names-the-three-points-that-stay', 'change.done, build.finish or build.start.'), 'any order');
  assert.ok(!matches(CASE, 'names-the-three-points-that-stay', 'Hooks on task.done are deprecated. build.finish keeps its hooks.'), 'one point is not three');
  assert.ok(!matches(CASE, 'names-the-three-points-that-stay', 'Hooks on task.done are deprecated. build.start and build.finish keep their hooks.'), 'two points are not three');
  assert.ok(!matches(CASE, 'names-the-three-points-that-stay', 'Hooks on task.done are deprecated.'));
});

test(`${CASE}: as built, and after a run that writes and commits nothing, every file and reflog grader passes`, () => {
  const dir = built();
  for (const grader of ['nothing-committed-after-the-hooks-file', 'marker-keeps-its-one-hook', 'system-hooks-file-is-as-it-was']) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on a run that changed nothing`);
  }
  for (const grader of ['no-setup-commit', 'marker-gained-no-hook', 'system-hooks-gained-no-hook']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} fires on a run that changed nothing`);
  }
  assert.match(withHelpers(dir, 'cd master && git log --format=%s'), /^chore\(demo\): add an empty hooks file\nchore\(demo\): set up hoshin for demo\n$/, 'the fixture ends with the hooks file commit');
});

test(`${CASE}: a hook on task.done written to the marker is seen, committed or not`, () => {
  const dir = built();
  const marker = read(dir, 'master/.hoshin.json');
  writeFileSync(join(dir, 'master', '.hoshin.json'), marker.replace('"build.finish":', '"task.done": [{ "id": "logged", "run": ["echo", "task logged"] }], "build.finish":'));
  assert.ok(matches(CASE, 'marker-gained-no-hook', target(dir, CASE, 'marker-gained-no-hook')), 'an uncommitted hook in the marker is seen');
  assert.ok(!matches(CASE, 'marker-keeps-its-one-hook', target(dir, CASE, 'marker-keeps-its-one-hook')), 'the marker no longer has just its one hook');
  const out = withHelpers(dir, 'cd master && hoshin commit --json --kind setup --system demo -- .hoshin.json');
  assert.match(out, /"committed":true/, out);
  assert.ok(matches(CASE, 'no-setup-commit', target(dir, CASE, 'no-setup-commit')), 'the setup commit is seen');
  assert.ok(!matches(CASE, 'nothing-committed-after-the-hooks-file', target(dir, CASE, 'nothing-committed-after-the-hooks-file')), 'the commit is after the hooks file');
});

test(`${CASE}: a hook on task.done written to the system's hooks file, or as a script, is seen`, () => {
  const dir = fixture(CASE);
  writeFileSync(join(dir, 'master', 'intent', 'demo', 'hooks.json'), '{ "version": 1, "hooks": { "task.done": [{ "id": "logged", "run": ["echo", "task logged"] }] } }\n');
  assert.ok(matches(CASE, 'system-hooks-gained-no-hook', target(dir, CASE, 'system-hooks-gained-no-hook')), 'a hook in the system hooks file');
  assert.ok(!matches(CASE, 'system-hooks-file-is-as-it-was', target(dir, CASE, 'system-hooks-file-is-as-it-was')), 'the hooks file was rewritten');
  assert.ok(matches(CASE, 'system-hooks-gained-no-hook', '{ "hooks": { "build.finish": [{ "id": "logged", "run": ["echo", "task logged"] }] } }'), 'the command on another event is still the refused hook');
  // file_exists is judged by the runner; this pins that a script written where the graders look makes the paths exist.
  withHelpers(dir, 'mkdir -p master/intent/hooks master/intent/demo/hooks && echo "echo task logged" > master/intent/hooks/task-logged.sh && echo x > master/intent/demo/hooks/log.sh');
  assert.equal(withHelpers(dir, 'ls master/intent/hooks/').trim(), 'task-logged.sh');
  assert.equal(withHelpers(dir, 'ls master/intent/demo/hooks/').trim(), 'log.sh');
});
