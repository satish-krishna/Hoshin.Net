// Lifecycle events: the hook configuration read from the base branch, layered and validated; `run` with its before
// hooks, built-in step and after hooks; the ledger and `status`. Each test builds a real workspace.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { CHANGE, commit, git, node, nodeHook, run, workspace, write } from './helpers/workspace.mjs';

const hooksIn = (hooks) => ({ hooks });

test('hooks lists the global hooks, then the system hooks, as committed on the base branch', () => {
  const { master } = workspace({
    marker: hooksIn({ 'build.finish': [nodeHook('unit', 'process.exit(0)', { repos: ['app'] })] }),
    masterFiles: { 'intent/demo/hooks.json': JSON.stringify({ version: 1, hooks: { 'build.finish': [nodeHook('lint', 'process.exit(0)')], 'change.done': [nodeHook('notify', '0', { when: 'after' })] } }) },
  });
  const r = run(master, ['hooks', '--json', '--system', 'demo']);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.json.hooks.map((h) => [h.id, h.layer, h.event, h.when, h.blocking, h.onFailure]), [
    ['unit', 'global', 'build.finish', 'before', true, 'fix'],
    ['lint', 'system', 'build.finish', 'before', true, 'fix'],
    ['notify', 'system', 'change.done', 'after', false, 'stop'],
  ]);
  assert.equal(r.json.gateRetries, 3);
  assert.equal(r.json.revision, 'refs/heads/main');
  const one = run(master, ['hooks', '--json', '--system', 'demo', '--event', 'change.done']);
  assert.deepEqual(one.json.hooks.map((h) => h.id), ['notify']);
});

test('a hook edited in the working files, or on another branch, is not the one that runs', () => {
  const { master } = workspace({ built: true, marker: hooksIn({ 'build.finish': [nodeHook('unit', '0')] }) });
  write(join(master, 'intent/demo/hooks.json'), JSON.stringify({ version: 1, hooks: { 'build.finish': [nodeHook('sneaky', '0')] } }));
  git(master, 'checkout', '-q', '-b', 'change/x');
  commit(master, { 'intent/demo/hooks.json': JSON.stringify({ version: 1, hooks: { 'build.finish': [nodeHook('sneaky', '0')] } }) });
  const r = run(master, ['hooks', '--json', '--system', 'demo']);
  assert.deepEqual(r.json.hooks.map((h) => h.id), ['unit']);
});

test('a system hook cannot reuse a global id, and nothing is replaced', () => {
  const { master } = workspace({
    marker: hooksIn({ 'build.finish': [nodeHook('unit', '0')] }),
    masterFiles: { 'intent/demo/hooks.json': JSON.stringify({ version: 1, hooks: { 'build.finish': [nodeHook('unit', 'process.exit(1)')] } }) },
  });
  const r = run(master, ['hooks', '--json', '--system', 'demo']);
  assert.equal(r.status, 1);
  assert.match(r.json.problems.join('\n'), /hook id unit is already used/);
});

test('every malformed entry is named, with its event and position', () => {
  const { master } = workspace({
    marker: {
      gateRetries: 99,
      hooks: {
        'build.done': [],
        'build.finish': [{ id: 'a', run: 'npm test' }, { id: 'b', run: ['x'], when: 'after', blocking: true }, { id: 'c', run: ['x'], timeout: 0, onFailure: 'retry', extra: 1 }],
      },
    },
  });
  const r = run(master, ['hooks', '--json', '--system', 'demo']);
  assert.equal(r.status, 1);
  const all = r.json.problems.join('\n');
  for (const want of [
    /build\.done is not an event/,
    /hooks\.build\.finish\[0\]: run must be a list/,
    /hooks\.build\.finish\[1\]: an after hook cannot block/,
    /hooks\.build\.finish\[2\]: unknown field extra/,
    /hooks\.build\.finish\[2\]: timeout must be/,
    /hooks\.build\.finish\[2\]: onFailure must be fix or stop/,
    /gateRetries must be/,
  ]) assert.match(all, want);
});

test('check refuses a malformed hook configuration before it is committed', () => {
  const { master } = workspace();
  write(join(master, 'intent/demo/hooks.json'), JSON.stringify({ version: 1, hooks: { 'build.finish': [{ id: 'x', run: [] }] } }));
  const r = run(master, ['check', join(master, 'intent/demo/hooks.json'), join(master, '.hoshin.json')]);
  assert.equal(r.status, 1);
  assert.match(r.stdout, /intent[\\/]demo[\\/]hooks\.json:1: hooks\.build\.finish\[0\]: run must be a list/);
  const clean = run(master, ['check', join(master, '.hoshin.json')]);
  assert.equal(clean.status, 0, clean.stdout);
});

test('run passes when every blocking before hook passes, and records it in the ledger at the head', () => {
  const { master, app } = workspace({ built: true, marker: hooksIn({ 'build.finish': [nodeHook('unit', 'process.exit(0)')] }) });
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.outcome, 'passed');
  assert.equal(r.json.repo, 'app');
  assert.equal(r.json.key, git(app, 'rev-parse', 'HEAD'));
  assert.deepEqual(r.json.hooks.map((h) => [h.id, h.outcome]), [['unit', 'passed']]);
  const ledger = join(git(master, 'rev-parse', '--path-format=absolute', '--git-common-dir'), 'hoshin', 'demo', CHANGE, 'app.json');
  assert.ok(existsSync(ledger), 'no ledger');
  const s = run(master, ['status', '--json', '--change', CHANGE]);
  assert.deepEqual(s.json.repos.map((r) => [r.repo, r.runs.map((x) => [x.event, x.outcome, x.current])]), [['app', [['build.finish', 'passed', true]]]]);
});

test('a failed blocking hook fails the event with what it asks for and the end of its output', () => {
  const lines = 'for (let i = 1; i <= 300; i++) console.log("line " + i); process.exitCode = 3';
  const { app } = workspace({ built: true, marker: hooksIn({ 'build.finish': [nodeHook('unit', `if (process.env.HOSHIN_BASELINE) process.exit(0); ${lines}`), nodeHook('never', 'process.exit(0)')] }) });
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(r.status, 1);
  assert.equal(r.json.outcome, 'failed');
  assert.equal(r.json.failed.id, 'unit');
  assert.equal(r.json.failed.onFailure, 'fix');
  assert.match(r.json.failed.output, /line 300$/);
  assert.match(r.json.failed.output, /^… 100 earlier lines left out/);
  assert.deepEqual(r.json.hooks.map((h) => h.id), ['unit'], 'a hook after the failed one ran');
  assert.deepEqual(r.json.problems, ['hook unit failed with exit code 3']);
});

test('a failed after hook is a warning, and a non-blocking before hook does not stop the event', () => {
  const { app } = workspace({
    built: true,
    marker: hooksIn({ 'build.finish': [nodeHook('soft', 'process.exit(1)', { blocking: false }), nodeHook('note', 'process.exit(1)', { when: 'after' })] }),
  });
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(r.status, 0, r.stdout);
  assert.equal(r.json.outcome, 'passed');
  assert.deepEqual(r.json.problems, ['after hook note failed with exit code 1; nothing was undone']);
});

test('a passed run is reused at the same head, rerun with --again, and run again after a commit', () => {
  const { app, ws } = workspace({ built: true });
  const counter = join(ws, 'count.txt');
  // The baseline run on the base commit is not counted: this test counts the runs at the head.
  const script = `if (!process.env.HOSHIN_BASELINE) { const f=${JSON.stringify(counter)};require('fs').appendFileSync(f,'x') }`;
  const { master } = { master: join(ws, 'master') };
  commit(master, { '.hoshin.json': JSON.stringify({ version: 1, systems: { demo: { repos: ['app'] } }, hooks: { 'build.finish': [nodeHook('count', script)] } }) }, 'chore(demo): hooks');
  run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  const again = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(again.json.reused, true);
  assert.equal(readFileSync(counter, 'utf8'), 'x');
  run(app, ['run', 'build.finish', '--json', '--change', CHANGE, '--again']);
  assert.equal(readFileSync(counter, 'utf8'), 'xx');
  commit(app, { 'a.txt': 'a' }, 'feat(demo): a');
  const moved = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(moved.json.reused, false);
  assert.equal(readFileSync(counter, 'utf8'), 'xxx');
  const s = run(master, ['status', '--json', '--change', CHANGE]);
  assert.deepEqual(s.json.repos[0].runs.map((x) => x.current), [false, false, true]);
});

test('a hook that runs past its time limit could not run, and its process is killed', () => {
  const { app } = workspace({ built: true, marker: hooksIn({ 'build.finish': [nodeHook('slow', 'setTimeout(() => {}, 60000)', { timeout: 1 })] }) });
  const started = Date.now();
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.ok(Date.now() - started < 20000, 'the hook was not stopped');
  assert.equal(r.status, 2);
  assert.equal(r.json.outcome, 'could-not-run');
  assert.match(r.json.failed.output, /time limit of 1 seconds/);
});

test('a hook whose program does not exist could not run', () => {
  const { app } = workspace({ built: true, marker: hooksIn({ 'build.finish': [{ id: 'gone', run: ['hoshin-no-such-program'] }] }) });
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(r.status, 2);
  assert.match(r.json.failed.output, /cannot start hoshin-no-such-program: ENOENT/);
});

test('a hook gets the event in its environment and on standard input, and runs in its repository', () => {
  const check = `
    const e = process.env; const input = JSON.parse(require('fs').readFileSync(0, 'utf8'));
    const ok = e.HOSHIN_EVENT === 'build.finish' && e.HOSHIN_REPO === 'app' && e.HOSHIN_SYSTEM === 'demo'
      && e.HOSHIN_CHANGE === '${CHANGE}' && e.HOSHIN_BRANCH === 'change/${CHANGE}' && e.HOSHIN_BASE === 'main' && e.HOSHIN_WHEN === 'before'
      && /^[0-9a-f]{40}$/.test(e.HOSHIN_HEAD) && input.HOSHIN_REPO === 'app' && require('path').basename(process.cwd()) === 'app'
      && e.HOSHIN_HOOKS_VERSION === '1';
    process.exit(ok ? 0 : 9);`;
  const { app } = workspace({ built: true, marker: hooksIn({ 'build.finish': [nodeHook('env', check)] }) });
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
});

test('a hook gets every HOSHIN_ variable also as AI_SDLC_, with the same value, and the AI_SDLC_FAILURES file works as HOSHIN_FAILURES does', () => {
  const check = `
    const e = process.env; const fs = require('fs');
    const names = Object.keys(e).filter((k) => k.startsWith('HOSHIN_') && k !== 'HOSHIN_GH' && k !== 'HOSHIN_RED_TIMEOUT');
    const same = names.length >= 14 && names.every((k) => e['AI_SDLC_' + k.slice('HOSHIN_'.length)] === e[k]);
    const ok = same && e.AI_SDLC_CHANGE === '${CHANGE}' && require('path').basename(e.AI_SDLC_REPO_PATH) === 'app' && e.AI_SDLC_HOOKS_VERSION === '1';
    if (!ok) process.exit(9);
    fs.writeFileSync(e.AI_SDLC_FAILURES, 'T1\\nT2\\n');
    process.exit(1);`;
  const { app } = workspace({ built: true, marker: hooksIn({ 'build.finish': [nodeHook('legacy', `if (process.env.HOSHIN_BASELINE) process.exit(0); ${check}`)] }) });
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(r.json.outcome, 'failed', JSON.stringify(r.json));
  assert.equal(r.json.hooks[0].exitCode, 1, 'the hook did not see the AI_SDLC_ variables');
  assert.deepEqual(r.json.hooks[0].ids, ['T1', 'T2']);
});

test('a script runs as committed on the base branch, from ${hooks}, even when the working copy is edited', () => {
  const { master, app } = workspace({
    built: true,
    marker: hooksIn({ 'build.finish': [{ id: 'script', run: [node, '${hooks}/check.mjs'] }] }),
    masterFiles: { 'intent/hooks/check.mjs': 'process.exit(0)\n' },
  });
  writeFileSync(join(master, 'intent/hooks/check.mjs'), 'process.exit(5)\n');
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
});

test('a hook limited to other repositories does not run', () => {
  const { app } = workspace({ built: true, repos: ['app', 'web'], marker: hooksIn({ 'build.finish': [nodeHook('web-only', 'process.exit(1)', { repos: ['web'] })] }) });
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(r.json.outcome, 'passed');
  assert.deepEqual(r.json.hooks, []);
});

test('--stream prints each hook as it starts and ends, then the outcome, and marks a check', () => {
  const { app } = workspace({ built: true, marker: hooksIn({ 'build.finish': [nodeHook('a', '0'), nodeHook('b', '0', { when: 'after' })] }) });
  const r = run(app, ['run', 'build.finish', '--json', '--change', CHANGE, '--stream']);
  assert.deepEqual(r.lines.map((l) => [l.type, l.id ?? null]), [['hook-start', 'a'], ['hook-end', 'a'], ['hook-start', 'b'], ['hook-end', 'b'], ['outcome', null]]);
  assert.deepEqual(r.lines.filter((l) => l.id === 'a').map((l) => l.check), [true, true]);
  assert.ok(r.lines.filter((l) => l.id === 'b').every((l) => !('check' in l)));
  assert.equal(r.json.outcome, 'passed');
});

test('doc.accept runs once per accepted commit of the document, and refuses one that is not committed', () => {
  const { master } = workspace({ marker: hooksIn({ 'doc.accept': [nodeHook('note', '0')] }) });
  const refused = run(master, ['run', 'doc.accept', '--json', '--change', CHANGE, '--document', 'spec.md']);
  assert.equal(refused.status, 1);
  assert.equal(refused.json.outcome, 'refused');
  assert.match(refused.json.problems[0], /spec\.md of .* is not committed/);
  const first = run(master, ['run', 'doc.accept', '--json', '--change', CHANGE, '--document', 'intent.md']);
  assert.equal(first.json.outcome, 'passed');
  assert.equal(first.json.subject, 'intent.md');
  assert.equal(first.json.key, git(master, 'log', '-1', '--format=%H', '--', `intent/demo/${CHANGE}/intent.md`));
  commit(master, { 'unrelated.txt': 'x' });
  const second = run(master, ['run', 'doc.accept', '--json', '--change', CHANGE, '--document', 'intent.md']);
  assert.equal(second.json.reused, true, 'a commit elsewhere made the acceptance run again');
});

test('a per-repository event outside a repository needs --repo, and only a repository of the system is accepted', () => {
  const { ws, master } = workspace();
  const none = run(ws, ['run', 'build.finish', '--json', '--change', CHANGE, '--master', master]);
  assert.equal(none.status, 2);
  assert.match(none.stderr, /pass --repo <repo>/);
  const stranger = run(master, ['run', 'build.finish', '--json', '--change', CHANGE, '--repo', 'web']);
  assert.equal(stranger.status, 2);
  assert.match(stranger.stderr, /web is not a repository of system demo/);
});

test('an unknown change is reported, and change.start cannot be run directly', () => {
  const { master } = workspace();
  const r = run(master, ['run', 'review.finish', '--json', '--change', '2026-01-01-none']);
  assert.equal(r.status, 2);
  assert.match(r.stderr, /no change 2026-01-01-none/);
  const start = run(master, ['run', 'change.start', '--json', '--change', CHANGE]);
  assert.equal(start.status, 2);
  assert.match(start.stderr, /usage:/);
});

// Deprecated hook points

const DEPRECATED = (id, event) => `hook ${id} runs on ${event}: hooks on ${event} are deprecated and the next major release removes them; the event itself stays`;

test('hooks warns of each hook on change.start, doc.accept, task.done and review.finish, and of no other', () => {
  const { master } = workspace({
    marker: hooksIn({ 'task.done': [nodeHook('each', '0')], 'build.finish': [nodeHook('unit', '0')] }),
    masterFiles: { 'intent/demo/hooks.json': JSON.stringify({ version: 1, hooks: { 'review.finish': [nodeHook('tell', '0', { when: 'after' })], 'change.start': [nodeHook('greet', '0')], 'doc.accept': [nodeHook('sign', '0')], 'build.start': [nodeHook('prep', '0')], 'change.done': [nodeHook('notify', '0')] } }) },
  });
  const r = run(master, ['hooks', '--json', '--system', 'demo']);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.json.problems, []);
  assert.deepEqual(r.json.warnings, [DEPRECATED('each', 'task.done'), DEPRECATED('tell', 'review.finish'), DEPRECATED('greet', 'change.start'), DEPRECATED('sign', 'doc.accept')]);
  const one = run(master, ['hooks', '--json', '--system', 'demo', '--event', 'task.done']);
  assert.deepEqual(one.json.warnings, [DEPRECATED('each', 'task.done')]);
  const none = run(master, ['hooks', '--json', '--system', 'demo', '--event', 'build.finish']);
  assert.deepEqual(none.json.warnings, []);
});

test('an event that ran a deprecated hook passes and says so in its problems; build.finish says nothing', () => {
  const { app } = workspace({
    built: true,
    marker: hooksIn({ 'task.done': [nodeHook('each', '0'), nodeHook('late', 'process.exit(1)', { when: 'after' })], 'build.finish': [nodeHook('unit', '0')] }),
  });
  const r = run(app, ['run', 'task.done', '--json', '--change', CHANGE, '--task', 'T1']);
  assert.equal(r.status, 0, r.stdout);
  assert.equal(r.json.outcome, 'passed');
  assert.deepEqual(r.json.problems, ['after hook late failed with exit code 1; nothing was undone', DEPRECATED('each', 'task.done'), DEPRECATED('late', 'task.done')]);
  const finish = run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.equal(finish.json.outcome, 'passed', finish.stdout);
  assert.deepEqual(finish.json.problems, []);
});

test('task.done with no hook has no problems', () => {
  const { app } = workspace({ built: true });
  const r = run(app, ['run', 'task.done', '--json', '--change', CHANGE, '--task', 'T1']);
  assert.equal(r.json.outcome, 'passed', r.stdout);
  assert.deepEqual(r.json.problems, []);
});
