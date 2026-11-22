// The harness: build.finish's own preconditions, the Stop gate, and the guard.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildLog, CHANGE, commit, dotnet, env, git, nodeHook, program, repo, run, workspace } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';
import { writeActiveRun } from './helpers/run-state.mjs';

function hook(command, input) {
  const r = spawnSync(dotnet, [program, command], { input: JSON.stringify(input), encoding: 'utf8', env });
  assert.equal(r.status, 0, r.stderr);
  return r.stdout.trim() === '' ? null : JSON.parse(r.stdout);
}
const gate = (cwd) => hook('gate', { hook_event_name: 'Stop', cwd, stop_hook_active: false });
const guardRaw = (payload) => hook('guard', { hook_event_name: 'PreToolUse', ...payload })?.hookSpecificOutput ?? null;
function activeGuard() {
  const { app } = workspace({ built: true });
  const hoshinDir = writeActiveRun(app, { sessionId: 's1' });
  const guard = (tool_name, tool_input) => guardRaw({ cwd: app, session_id: 's1', tool_name, tool_input });
  return { app, hoshinDir, guard };
}
const finish = (app, ...more) => run(app, ['run', 'build.finish', '--json', '--change', CHANGE, ...more]);
const startBuild = (app, master) => run(app, ['run', 'build.start', '--json', '--change', CHANGE, '--master', master]);

test('build.finish refuses while a task has no build log entry, and a commit that breaks the commit rules', () => {
  const { master, app } = workspace({ built: true, masterFiles: { [`intent/demo/${CHANGE}/build/app.md`]: buildLog(['T1']) } });
  let r = finish(app);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems[0], /app still has tasks without a build log entry: T2, T3/);
  commit(master, { [`intent/demo/${CHANGE}/build/app.md`]: buildLog(['T1', 'T2', 'T3']) });
  commit(app, { 'x.txt': 'x' }, 'feat: no scope');
  r = finish(app);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems[0], /break the commit rules .*feat: no scope: its header does not carry the scope demo/);
});

test("build.finish takes a Generated-By: ai-sdlc trailer as it takes hoshin's, and still refuses a commit with neither", () => {
  const { app } = workspace({ built: true, masterFiles: { [`intent/demo/${CHANGE}/build/app.md`]: buildLog(['T1', 'T2', 'T3']) } });
  writeFileSync(join(app, 'old.txt'), 'x');
  git(app, 'add', '.');
  git(app, 'commit', '-q', '-m', 'feat(demo): from the old plugin', '-m', 'Generated-By: ai-sdlc');
  let r = finish(app);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  writeFileSync(join(app, 'bare.txt'), 'x');
  git(app, 'add', '.');
  git(app, 'commit', '-q', '-m', 'feat(demo): by hand', '-m', 'Generated-By: someone-else');
  r = finish(app);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems[0], /feat\(demo\): by hand: it has no Generated-By: hoshin trailer/);
});

test('the gate lets a session stop when no build is active, or while the build is unfinished', () => {
  const { ws, master, app } = workspace({ built: true, masterFiles: { [`intent/demo/${CHANGE}/build/app.md`]: buildLog(['T1']) } });
  assert.equal(gate(ws), null, 'outside a repository');
  assert.equal(gate(app), null, 'no build.start yet');
  startBuild(app, master);
  assert.equal(gate(app), null, 'T2 and T3 are still to do');
});

test('the gate blocks a finished build whose checks have not run, and lets it stop once they pass', () => {
  const { master, app } = workspace({ built: true, marker: { hooks: { 'build.finish': [nodeHook('unit', '0')] } } });
  startBuild(app, master);
  const blocked = gate(app);
  assert.equal(blocked.decision, 'block');
  assert.match(blocked.reason, /build\.finish event has not run/);
  assert.match(blocked.reason, /run build\.finish --json --change 2026-11-01-greeting --repo app --master/);
  assert.equal(finish(app).json.outcome, 'passed');
  assert.equal(gate(app), null);
  commit(app, { 'later.txt': 'x' }, 'fix(demo): later');
  assert.equal(gate(app)?.decision, 'block', 'a commit after the checks made them stale');
});

test('the gate sends the agent back to fix a failing check, up to gateRetries times per head, then lets it stop', () => {
  const { master, app } = workspace({ built: true, marker: { gateRetries: 2, hooks: { 'build.finish': [nodeHook('unit', 'if (!process.env.HOSHIN_BASELINE) { console.log("expected 2, got 3"); process.exitCode = 1 }')] } } });
  startBuild(app, master);
  assert.equal(finish(app).json.outcome, 'failed');
  for (const attempt of [1, 2]) {
    const blocked = gate(app);
    assert.equal(blocked?.decision, 'block', `attempt ${attempt}`);
    assert.match(blocked.reason, new RegExp(`The check unit failed on app .* \\(attempt ${attempt} of 2\\)`));
    assert.match(blocked.reason, /expected 2, got 3/);
  }
  const done = gate(app);
  assert.equal(done.decision, undefined);
  assert.match(done.systemMessage, /hook unit still fails on app after 2 attempts/);
});

test('a check that asks to stop, or could not run, does not hold the agent', () => {
  const { master, app } = workspace({ built: true, marker: { hooks: { 'build.finish': [nodeHook('deploy-check', 'process.exitCode = 1', { onFailure: 'stop' })] } } });
  startBuild(app, master);
  assert.equal(finish(app).json.failed.onFailure, 'stop');
  assert.equal(gate(app), null);
});

test('the gate ignores a repository that has moved off the change branch', () => {
  const { master, app } = workspace({ built: true });
  startBuild(app, master);
  git(app, 'checkout', '-q', 'main');
  assert.equal(gate(app), null);
});

test('the guard stays silent and writes no state in a plain git repo with no active hoshin run', () => {
  const dir = repo(join(tempDir('hoshin-plain-'), 'plain'));
  const hoshinDir = join(dir, '.git', 'hoshin');

  assert.equal(guardRaw({ cwd: dir, session_id: 's1', tool_name: 'Bash', tool_input: { command: 'git push origin main' } }), null);
  assert.equal(existsSync(hoshinDir), false, '.git/hoshin must not be created after git push in a plain repo');

  assert.equal(guardRaw({ cwd: dir, session_id: 's1', tool_name: 'Bash', tool_input: { command: 'gh pr create --title x --body y' } }), null);
  assert.equal(existsSync(hoshinDir), false, '.git/hoshin must not be created after gh pr create in a plain repo');

  assert.equal(guardRaw({ cwd: dir, session_id: 's1', tool_name: 'Edit', tool_input: { file_path: join(dir, '.hoshin.json') } }), null);
  assert.equal(existsSync(hoshinDir), false, '.git/hoshin must not be created after Edit .hoshin.json in a plain repo');
});

test('the guard applies only to the active run session and logs static denials', () => {
  const { app, hoshinDir } = activeGuard();

  assert.equal(
    guardRaw({ cwd: app, session_id: 's2', tool_name: 'Bash', tool_input: { command: 'git push origin main' } }),
    null,
    'unrelated session s2 must not be denied',
  );

  const denied = guardRaw({ cwd: app, session_id: 's1', tool_name: 'Bash', tool_input: { command: 'git push origin main' } });
  assert.equal(denied?.permissionDecision, 'deny');

  const logPath = join(hoshinDir, 'hooks.jsonl');
  assert.equal(existsSync(logPath), true, 'static denial must be traced in hooks.jsonl');
  assert.match(readFileSync(logPath, 'utf8'), /deny: hoshin: Pushing happens only/);
});

test('the guard refuses edits to the hook configuration and the change record, and allows other files', () => {
  const { guard } = activeGuard();
  for (const path of ['/w/master/.hoshin.json', '/w/master/intent/hooks/check.sh', '/w/master/intent/demo/hooks.json', '/w/master/intent/demo/hooks/lint.mjs', `/w/master/intent/demo/${CHANGE}/change.jsonc`, `/w/master/intent/demo/${CHANGE}/change.yml`, '/w/master/.git/hoshin/demo/c/app.json', '/w/master/.git/hoshin/demo/c/checks.jsonl', 'C:\\w\\master\\intent\\demo\\hooks.json']) {
    assert.equal(guard('Edit', { file_path: path })?.permissionDecision, 'deny', path);
  }
  for (const path of ['/w/app/src/hooks/useThing.ts', `/w/master/intent/demo/${CHANGE}/build/app.md`, '/w/app/hooks.json', '/w/master/intent/demo/context/app.md']) {
    assert.equal(guard('Write', { file_path: path }), null, path);
  }
});

test('the guard refuses pushing, GitHub writes, skipping git hooks, and shell writes to protected files', () => {
  const { guard } = activeGuard();
  for (const command of [
    'git push -u origin feat/x',
    'git -C /w/app push',
    'cd /w/app && git push --force',
    'gh pr create --title x --body y',
    'gh pr merge 7',
    'gh api -X POST repos/o/r/pulls',
    'git commit --no-verify -m "x"',
    'git -C /w/app commit -n -m "x"',
    'git -c core.hooksPath=/dev/null commit -m x',
    'echo {} > /w/master/.hoshin.json',
    'sed -i s/a/b/ intent/demo/hooks.json',
    'rm intent/hooks/check.sh',
    `git add intent/demo/${CHANGE}/change.jsonc`,
    'echo x >> /w/master/.git/hoshin/demo/c/checks.jsonl',
    `node -e "require('fs').appendFileSync('/w/master/.git/hoshin/demo/c/checks.jsonl','x')"`,
  ]) assert.equal(guard('Bash', { command })?.permissionDecision, 'deny', command);
  for (const command of [
    'git commit -m "docs(demo): push the button" -m "Generated-By: hoshin"',
    'git -C /w/master commit -q -m "docs(demo): log T1 of c" -m "Generated-By: hoshin" -- intent/demo/c/build/app.md',
    'cat .hoshin.json',
    'git show main:intent/demo/hooks.json',
    'gh pr view 7 --json state',
    'dotnet /p/hoshin.dll hooks --json --system demo',
    'git log --oneline -n 5',
    'npm test 2>&1 | tail -20',
  ]) assert.equal(guard('Bash', { command }), null, command);
});

test('the gate and the guard let the session go on when their input is not JSON', () => {
  for (const command of ['gate', 'guard']) {
    const r = spawnSync(dotnet, [program, command], { input: 'not json', encoding: 'utf8', env });
    assert.equal(r.status, 0);
    assert.equal(r.stdout, '');
  }
});

test('the program path the gate prints is the one invoked', () => {
  const { master, app } = workspace({ built: true });
  startBuild(app, master);
  assert.ok(gate(app).reason.includes(`dotnet "${program}" run build.finish`), gate(app).reason);
});

test('the guard keeps baseline approve and retake for the engineer, and allows baseline show', () => {
  const { guard } = activeGuard();
  const why = 'hoshin: Only the engineer approves a check that already fails on the base, or retakes a baseline: show them the command and stop.';
  for (const command of [
    'dotnet /p/hoshin.dll baseline approve --json --change c --repo app --hook boot',
    'hoshin baseline retake --json --change c --repo app',
    'cd /w/app && dotnet "/p/hoshin.dll" baseline  approve --json --change c --repo app --hook boot',
  ]) {
    const out = guard('Bash', { command });
    assert.equal(out?.permissionDecision, 'deny', command);
    assert.equal(out?.permissionDecisionReason, why);
  }
  assert.equal(guard('Bash', { command: 'hoshin baseline show --json --change c --repo app' }), null);
});
