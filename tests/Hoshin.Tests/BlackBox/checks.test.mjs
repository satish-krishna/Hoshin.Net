// The checks command: reads a change's check events back, grouped by run.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { CHANGE, buildLog, commit, dotnet, git, node, nodeHook, program, run, workspace } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';
import { tempDir } from './helpers/temp.mjs';

const checksPath = (master) => join(git(master, 'rev-parse', '--path-format=absolute', '--git-common-dir'), 'hoshin', 'demo', CHANGE, 'checks.jsonl');
const append = (master, text) => {
  mkdirSync(dirname(checksPath(master)), { recursive: true });
  appendFileSync(checksPath(master), text);
};
const lines = (master, ...objects) => append(master, objects.map((o) => JSON.stringify(o) + '\n').join(''));

const start = (runId, check, extra = {}) => ({ type: 'check-start', run: runId, event: 'build.finish', repo: 'app', check, phase: 'baseline', at: new Date().toISOString(), pid: process.pid, timeout: 600, ...extra });
const end = (runId, check, extra = {}) => ({ type: 'check-end', run: runId, event: 'build.finish', repo: 'app', check, phase: 'baseline', at: new Date().toISOString(), outcome: 'passed', ids: null, blocks: null, ...extra });

const checks = (cwd, ...extra) => run(cwd, ['checks', '--json', '--change', CHANGE, ...extra]);

test('with no check events, checks prints no runs', () => {
  const { app } = workspace();
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json, { system: 'demo', change: CHANGE, runs: [], problems: [] });
});

test('a build.finish run reads back as one head run, each check ended with its outcome', () => {
  const failing = `if (process.env.HOSHIN_BASELINE) process.exit(0); require('fs').writeFileSync(process.env.HOSHIN_FAILURES, 'x\\n'); process.exit(1)`;
  const { app } = workspace({ built: true, marker: { hooks: { 'build.finish': [nodeHook('a', 'process.exit(0)'), nodeHook('b', failing)] } } });
  run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  const head = r.json.runs.filter((x) => x.phase === 'head');
  assert.equal(head.length, 1);
  assert.equal(head[0].repo, 'app');
  assert.equal(head[0].event, 'build.finish');
  assert.deepEqual(
    head[0].checks.map((c) => [c.check, c.state, c.outcome, c.ids, c.blocks]),
    [['a', 'ended', 'passed', null, false], ['b', 'ended', 'failed', ['x'], true]],
  );
  for (const c of head[0].checks) {
    assert.equal(typeof c.startedAt, 'string');
    assert.equal(typeof c.endedAt, 'string');
  }
});

test('a start with no end whose program is alive and within its timeout is running', () => {
  const { master, app } = workspace();
  lines(master, start('r1', 'a'), end('r1', 'a'), start('r1', 'b'));
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.runs.map((x) => [x.run, x.phase, x.checks.map((c) => [c.check, c.state])]), [['r1', 'baseline', [['a', 'ended'], ['b', 'running']]]]);
  const b = r.json.runs[0].checks[1];
  assert.deepEqual([b.endedAt, b.outcome, b.ids, b.blocks, b.timeout], [null, null, null, null, 600]);
});

test('a start whose program is gone is abandoned', () => {
  const { master, app } = workspace();
  const gone = spawnSync(node, ['-e', 'process.exit(0)']).pid;
  lines(master, start('r1', 'a', { pid: gone }));
  assert.equal(checks(app).json.runs[0].checks[0].state, 'abandoned');
});

test('a start past its timeout is abandoned', () => {
  const { master, app } = workspace();
  lines(master, start('r1', 'a', { at: new Date(Date.now() - 20_000).toISOString(), timeout: 10 }));
  assert.equal(checks(app).json.runs[0].checks[0].state, 'abandoned');
});

test('a later run for the same repository and phase abandons an open start; another phase does not', () => {
  const { master, app } = workspace();
  lines(master, start('r1', 'a'), start('r2', 'a', { phase: 'head' }), start('r3', 'a'));
  const r = checks(app);
  assert.deepEqual(r.json.runs.map((x) => [x.run, x.checks[0].state]), [['r1', 'abandoned'], ['r2', 'running'], ['r3', 'running']]);
});

test('a half-written last line and a line of an unknown type are skipped without a problem', () => {
  const { master, app } = workspace();
  lines(master, start('r1', 'a'), { type: 'check-note', run: 'r1' });
  append(master, JSON.stringify(end('r1', 'a')).slice(0, 20));
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, []);
  assert.equal(r.json.runs[0].checks[0].state, 'running');
});

test('a complete line that is not a check event is reported and skipped, and the rest is read', () => {
  const { master, app } = workspace();
  append(master, 'not json\n');
  lines(master, start('r1', 'a'), end('r1', 'a'));
  const r = checks(app);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.json.problems.length, 1);
  assert.match(r.json.problems[0], /line 1 of .*checks\.jsonl is not a check event/);
  assert.equal(r.json.runs[0].checks[0].state, 'ended');
});

test('--repo keeps the runs of that repository only', () => {
  const { master, app } = workspace({ repos: ['app', 'web'] });
  lines(master, start('r1', 'a'), start('r2', 'a', { repo: 'web' }));
  const r = checks(app, '--repo', 'web');
  assert.deepEqual(r.json.runs.map((x) => [x.run, x.repo]), [['r2', 'web']]);
});

test('checks needs --json and --change', () => {
  const { app } = workspace();
  assert.equal(run(app, ['checks', '--change', CHANGE]).status, 2);
  assert.equal(run(app, ['checks', '--json']).status, 2);
});

test('a line ending in CR still reads', () => {
  const { master, app } = workspace();
  append(master, JSON.stringify(start('r1', 'a')) + '\r\n');
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.runs.map((x) => [x.run, x.checks[0].check]), [['r1', 'a']]);
});

test('a blank line is skipped without a problem', () => {
  const { master, app } = workspace();
  append(master, '\n');
  lines(master, start('r1', 'a'));
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, []);
  assert.equal(r.json.runs.length, 1);
});

test('an end with no start is skipped without a problem', () => {
  const { master, app } = workspace();
  lines(master, end('r1', 'a'));
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.runs, []);
  assert.deepEqual(r.json.problems, []);
});

test('an empty file gives no runs', () => {
  const { master, app } = workspace();
  append(master, '');
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.runs, []);
  assert.deepEqual(r.json.problems, []);
});

const SR_NAME = '2026-11-02-wave';
const SR_PLAN =
  '```jsonc\n{ "layout": 2, "system": "demo" }\n```\n\n# Plan: Wave\n\n## Tasks\n\n### T1: Api\n\n```jsonc\n{\n  "id": "T1",\n  "repo": "api",\n  "depends_on": []\n}\n```\n\n#### Change\n\nChange the api.\n\n#### Tests\n\nTest it first.\n';

test('in the single-repo layout, checks works from inside a baseline check and sees the run as running', () => {
  const out = join(tempDir('hoshin-checks-out-'), 'seen.json');
  const script = `if (process.env.HOSHIN_BASELINE) { const r = require('child_process').spawnSync(${JSON.stringify(dotnet)}, [${JSON.stringify(program)}, 'checks', '--json', '--change', ${JSON.stringify(SR_NAME)}], { encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1' } }); require('fs').writeFileSync(${JSON.stringify(out)}, JSON.stringify({ status: r.status, stdout: r.stdout, stderr: r.stderr })) }`;
  const s = singleRepo({ marker: { hooks: { 'build.finish': [nodeHook('probe', script)] } } });
  const started = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', SR_NAME, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Wave']);
  assert.equal(started.json.outcome, 'passed', JSON.stringify(started.json));
  commit(started.json.details.copy, { [`intent/demo/${SR_NAME}/plan.md`]: SR_PLAN, [`intent/demo/${SR_NAME}/build/api.md`]: buildLog(['T1']) }, 'docs(PROJ-1): plan');
  const r = run(s.repo, ['run', 'build.start', '--json', '--change', SR_NAME, '--repo', 'api']);
  assert.equal(r.json.outcome, 'passed', r.stdout + r.stderr);
  const seen = JSON.parse(readFileSync(out, 'utf8'));
  assert.equal(seen.status, 0, seen.stdout + seen.stderr);
  const printed = JSON.parse(seen.stdout);
  assert.equal(printed.system, 'demo');
  assert.deepEqual(printed.runs.map((x) => [x.phase, x.checks.map((c) => [c.check, c.state])]), [['baseline', [['probe', 'running']]]]);
});

test('with no ledger folder for the change, checks still exits 2', () => {
  const s = singleRepo();
  const r = run(s.repo, ['checks', '--json', '--change', 'no-such-change']);
  assert.equal(r.status, 2, r.stdout + r.stderr);
  assert.match(r.stdout + r.stderr, /no change no-such-change/);
});

test('with several systems holding a ledger and no --system, checks asks for --system; --system picks one', () => {
  const s = singleRepo({ marker: { systems: { demo: { apps: { api: 'apps/api' } }, other: { apps: { web: 'apps/web' } } } } });
  const common = git(s.repo, 'rev-parse', '--path-format=absolute', '--git-common-dir');
  for (const system of ['demo', 'other']) mkdirSync(join(common, 'hoshin', system, 'ghost'), { recursive: true });
  writeFileSync(join(common, 'hoshin', 'other', 'ghost', 'checks.jsonl'), JSON.stringify(start('r9', 'z', { repo: 'web' })) + '\n');
  const both = run(s.repo, ['checks', '--json', '--change', 'ghost']);
  assert.equal(both.status, 2, both.stdout + both.stderr);
  assert.match(both.stdout + both.stderr, /several systems \(demo, other\); pass --system/);
  const one = run(s.repo, ['checks', '--json', '--change', 'ghost', '--system', 'other']);
  assert.equal(one.status, 0, one.stdout + one.stderr);
  assert.equal(one.json.system, 'other');
  assert.equal(one.json.runs.length, 1);
});

test('a start line missing at, repo, phase, event or a finite timeout, or an end line missing at, repo, phase or event, is not a check event', () => {
  const { master, app } = workspace();
  const bad = (type, drop, extra = {}) => {
    const line = type === 'start' ? start('r1', 'a', extra) : end('r1', 'a', extra);
    delete line[drop];
    return line;
  };
  lines(master, bad('start', 'at'), bad('start', 'repo'), bad('start', 'phase'), bad('start', 'event'), bad('start', 'timeout'), start('r1', 'a', { timeout: 'soon' }), bad('end', 'at'), bad('end', 'repo'), bad('end', 'phase'), bad('end', 'event'));
  const r = checks(app);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.json.problems.length, 10);
  assert.match(r.json.problems[0], /^line 1 of .*checks\.jsonl is not a check event$/);
  assert.deepEqual(r.json.runs, []);
});

test('a pid that is not a positive integer counts as not running, and the start line still reads', () => {
  const { master, app } = workspace();
  lines(master, start('r1', 'a', { pid: 0 }), start('r2', 'a', { repo: 'web', pid: -5 }), start('r3', 'a', { repo: 'x', pid: 'abc' }), start('r4', 'a', { repo: 'y', pid: 1.5 }));
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.runs.map((x) => x.checks[0].state), ['abandoned', 'abandoned', 'abandoned', 'abandoned']);
});

test('a leading byte order mark does not make the first line a problem', () => {
  const { master, app } = workspace();
  append(master, '\uFEFF' + JSON.stringify(start('r1', 'a')) + '\n');
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, []);
  assert.equal(r.json.runs[0].checks[0].state, 'running');
});

test('a later run for another repository in the same phase does not abandon an open start', () => {
  const { master, app } = workspace({ repos: ['app', 'web'] });
  lines(master, start('r1', 'a'), start('r2', 'a', { repo: 'web' }));
  const r = checks(app);
  assert.deepEqual(r.json.runs.map((x) => [x.run, x.repo, x.checks[0].state]), [['r1', 'app', 'running'], ['r2', 'web', 'running']]);
});

test('the timeout is in seconds: a start a few seconds old with a 600 second timeout is running', () => {
  const { master, app } = workspace();
  lines(master, start('r1', 'a', { at: new Date(Date.now() - 5_000).toISOString(), timeout: 600 }));
  assert.equal(checks(app).json.runs[0].checks[0].state, 'running');
});

test('a build.finish that takes a baseline reads back a baseline run whose checks are ended with blocks null', () => {
  const { app } = workspace({ built: true, marker: { hooks: { 'build.finish': [nodeHook('a', 'process.exit(0)')] } } });
  run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  const r = checks(app);
  const baseline = r.json.runs.filter((x) => x.phase === 'baseline');
  assert.equal(baseline.length, 1);
  assert.deepEqual(baseline[0].checks.map((c) => [c.check, c.state, c.blocks]), [['a', 'ended', null]]);
  for (const x of r.json.runs) for (const c of x.checks) assert.equal(c.state, 'ended');
});

test('interleaved runs: runs are ordered by their first line and each check pairs with its own run end', () => {
  const { master, app } = workspace({ repos: ['app', 'web'] });
  lines(master, start('r1', 'a'), start('r2', 'a', { repo: 'web' }), end('r2', 'a', { repo: 'web', outcome: 'failed', blocks: true }), start('r1', 'b'), end('r1', 'a'), end('r1', 'b', { outcome: 'failed' }));
  const r = checks(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.runs.map((x) => [x.run, x.checks.map((c) => [c.check, c.state, c.outcome])]), [
    ['r1', [['a', 'ended', 'passed'], ['b', 'ended', 'failed']]],
    ['r2', [['a', 'ended', 'failed']]],
  ]);
});
