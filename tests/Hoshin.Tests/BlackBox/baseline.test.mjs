// The baseline of a repository's checks: what each blocking before hook of build.finish does on the base commit,
// so that at build.finish a failure that was already there is told apart from a new one. Hooks say what failed by
// writing IDs, one per line, to the file named by HOSHIN_FAILURES.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { CHANGE, commit, dotnet, env, git, program, run, workspace } from './helpers/workspace.mjs';
import { nodeHook } from './helpers/workspace.mjs';

const hooksIn = (hooks) => ({ hooks });
const finish = (app) => run(app, ['run', 'build.finish', '--json', '--change', CHANGE]);

test("a failed hook's IDs are the lines it writes to HOSHIN_FAILURES", () => {
  const write = `require('fs').writeFileSync(process.env.HOSHIN_FAILURES, 'b\\n a \\n\\nb\\n'); process.exit(1)`;
  const { app } = workspace({ built: true, marker: hooksIn({ 'build.finish': [nodeHook('unit', write)] }) });
  // It fails the same way on the base, so the baseline lets it through; this test is about the IDs it read.
  const r = finish(app);
  assert.deepEqual(r.json.hooks[0].ids, ['b', 'a'], r.stdout + r.stderr);
});

test("a failed hook that writes nothing has no IDs, and a passed hook's IDs are ignored", () => {
  const passes = `require('fs').writeFileSync(process.env.HOSHIN_FAILURES, 'x'); process.exit(0)`;
  const { app } = workspace({ built: true, marker: hooksIn({ 'build.finish': [nodeHook('green', passes), nodeHook('red', 'process.exit(1)')] }) });
  const r = finish(app);
  assert.deepEqual(r.json.hooks.map((h) => [h.id, h.ids]), [['green', null], ['red', null]]);
});

test('each hook gets its own IDs file', () => {
  const writes = (id) => `require('fs').writeFileSync(process.env.HOSHIN_FAILURES, '${id}'); process.exit(1)`;
  const { app } = workspace({
    built: true,
    marker: hooksIn({ 'build.finish': [nodeHook('a', writes('one'), { blocking: false }), nodeHook('b', writes('two'), { blocking: false })] }),
  });
  const r = finish(app);
  assert.deepEqual(r.json.hooks.map((h) => [h.id, h.ids]), [['a', ['one']], ['b', ['two']]]);
});

// A check that fails when the repository holds broken.txt, reporting its lines as IDs (or nothing, with ids: false).
const brokenCheck = (id = 'boot', { ids = true, ...rest } = {}) =>
  nodeHook(id, `const fs=require('fs'); if (fs.existsSync('broken.txt')) { ${ids ? "fs.writeFileSync(process.env.HOSHIN_FAILURES, fs.readFileSync('broken.txt','utf8'));" : ''} process.exit(1) }`, rest);

/** A workspace whose app has broken.txt on its base branch, with the app on the change's branch and no commits on it. */
function broken(checks, { content = 'r1\n' } = {}) {
  const w = workspace({ marker: hooksIn({ 'build.finish': checks }) });
  commit(w.app, { 'broken.txt': content }, 'chore: broken on main');
  git(w.app, 'push', '-q', 'origin', 'main');
  git(w.app, 'checkout', '-q', '-b', `change/${CHANGE}`);
  return w;
}

const baseline = (cwd, verb, ...more) => run(cwd, ['baseline', verb, '--json', '--change', CHANGE, '--repo', 'app', ...more]);
const storeFile = (master) => join(git(master, 'rev-parse', '--path-format=absolute', '--git-common-dir'), 'hoshin', 'demo', '.baselines', 'app.json');

test('retake on a fresh build takes the baseline in place', () => {
  const { master, app } = broken([brokenCheck()]);
  const head = git(app, 'rev-parse', 'HEAD');
  const r = baseline(app, 'retake');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.taken, true);
  assert.equal(r.json.base, head);
  assert.deepEqual(r.json.hooks.map(({ output, ...h }) => h), [{ id: 'boot', outcome: 'failed', exitCode: 1, ids: ['r1'] }]);
  assert.equal(typeof r.json.hooks[0].output, 'string');
  assert.ok(existsSync(storeFile(master)), 'no store');
  assert.equal(git(app, 'rev-parse', 'HEAD'), head);
});

test("baseline show prints each failing check's exit code and output", () => {
  const failing = (id, line) => nodeHook(id, `if (require('fs').existsSync('broken.txt')) { console.log('${line}'); process.exit(1) }`);
  const { app } = broken([failing('alpha', 'alpha-line'), failing('beta', 'beta-line')]);
  baseline(app, 'retake');
  const r = baseline(app, 'show');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.hooks.map((h) => [h.id, h.exitCode]), [['alpha', 1], ['beta', 1]]);
  assert.match(r.json.hooks[0].output, /alpha-line/);
  assert.doesNotMatch(r.json.hooks[0].output, /beta-line/);
  assert.match(r.json.hooks[1].output, /beta-line/);
  assert.doesNotMatch(r.json.hooks[1].output, /alpha-line/);
});

test('show reuses the baseline, and a changed check takes a new one', () => {
  const { master, app } = broken([brokenCheck()]);
  baseline(app, 'retake');
  const shown = baseline(app, 'show');
  assert.equal(shown.json.taken, true);
  assert.equal(shown.json.reused, true);
  commit(master, { '.hoshin.json': JSON.stringify({ version: 1, systems: { demo: { repos: ['app'] } }, hooks: { 'build.finish': [brokenCheck('boot', { timeout: 30 })] } }, null, 2) + '\n' }, 'chore(demo): change the check');
  assert.equal(baseline(app, 'show').json.taken, false);
});

test('a branch with commits takes it at the merge-base and returns to the branch, even when a check fails', () => {
  const { app } = broken([brokenCheck()]);
  const base = git(app, 'rev-parse', 'HEAD');
  git(app, 'rm', '-q', 'broken.txt');
  commit(app, {}, 'fix(demo): remove broken');
  const r = baseline(app, 'retake');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.base, base);
  assert.deepEqual(r.json.hooks.map(({ output, ...h }) => h), [{ id: 'boot', outcome: 'failed', exitCode: 1, ids: ['r1'] }]);
  assert.equal(git(app, 'branch', '--show-current'), `change/${CHANGE}`);
  assert.ok(!existsSync(join(app, 'broken.txt')), 'the branch work is not back');
});

test('uncommitted files refuse the baseline', () => {
  const { master, app } = broken([brokenCheck()]);
  writeFileSync(join(app, 'x.txt'), 'x');
  const r = baseline(app, 'retake');
  assert.equal(r.status, 1);
  assert.match(r.json.problems.join('\n'), /x\.txt/);
  assert.ok(!existsSync(storeFile(master)), 'a baseline was stored');
});

test('a check that could not run is not stored', () => {
  const { master, app } = broken([{ id: 'boot', run: ['no-such-program-xyz'] }]);
  const r = baseline(app, 'retake');
  assert.equal(r.status, 2, r.stdout + r.stderr);
  assert.equal(r.json.taken, false);
  assert.ok(!existsSync(storeFile(master)), 'a fault of the machine was stored');
});

test('the master repository gets no baseline', () => {
  const { master } = broken([brokenCheck()]);
  const r = run(master, ['baseline', 'show', '--json', '--change', CHANGE, '--repo', 'master']);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.taken, false);
  assert.deepEqual(r.json.problems, []);
});

test('approve records an approval for a red check without IDs, and show lists it', () => {
  const { app } = broken([brokenCheck('boot', { ids: false })]);
  baseline(app, 'retake');
  assert.deepEqual(baseline(app, 'show').json.needsApproval, ['boot']);
  const r = baseline(app, 'approve', '--hook', 'boot');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.approved, { hook: 'boot', base: git(app, 'rev-parse', 'HEAD') });
  const shown = baseline(app, 'show').json;
  assert.deepEqual([shown.needsApproval, shown.approved], [[], ['boot']]);
  assert.equal(baseline(app, 'approve', '--hook', 'boot').status, 0, 'approving twice');
  assert.deepEqual(baseline(app, 'show').json.approved, ['boot']);
});

test('approve refuses a check that is green, has IDs, or is not in the baseline', () => {
  const { app } = broken([brokenCheck('boot'), brokenCheck('quiet', { ids: false }), nodeHook('green', 'process.exit(0)')]);
  baseline(app, 'retake');
  for (const hook of ['boot', 'green', 'nope']) {
    const r = baseline(app, 'approve', '--hook', hook);
    assert.equal(r.status, 1, hook);
    assert.equal(r.json.approved, null, hook);
    assert.ok(r.json.problems.length > 0, hook);
  }
  assert.deepEqual(baseline(app, 'show').json.approved, []);
  assert.deepEqual(baseline(app, 'show').json.needsApproval, ['quiet']);
});

test('show reads a ledger written before approvals existed', () => {
  const { master, app } = broken([brokenCheck('boot', { ids: false })]);
  baseline(app, 'retake');
  const folder = join(git(master, 'rev-parse', '--path-format=absolute', '--git-common-dir'), 'hoshin', 'demo', CHANGE);
  mkdirSync(folder, { recursive: true });
  writeFileSync(join(folder, 'app.json'), JSON.stringify({ version: 1, system: 'demo', change: CHANGE, repo: 'app', path: app, runs: [], gateBlocks: {} }));
  const r = baseline(app, 'show');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.needsApproval, ['boot']);
});

/** Like `broken`, but the app stays on its base branch, for build.start to put it on the change's branch. */
function brokenOnMain(checks, options) {
  const w = broken(checks, options);
  git(w.app, 'checkout', '-q', 'main');
  git(w.app, 'branch', '-q', '-D', `change/${CHANGE}`);
  return w;
}

const start = (app, ...more) => run(app, ['run', 'build.start', '--json', '--change', CHANGE, ...more]);

test('build.start takes the baseline and passes when every check is green or says what failed', () => {
  const { app } = brokenOnMain([brokenCheck('boot'), nodeHook('green', 'process.exit(0)')]);
  const r = start(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.outcome, 'passed');
  assert.equal(git(app, 'branch', '--show-current'), `change/${CHANGE}`);
  assert.deepEqual(r.json.details.baseline, { base: git(app, 'rev-parse', 'HEAD'), reused: false, failing: [{ id: 'boot', ids: ['r1'] }], approved: [] });
});

test('build.start stops on a red check without IDs and names the approve command', () => {
  const { app } = brokenOnMain([brokenCheck('boot', { ids: false })]);
  const r = start(app);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.json.outcome, 'failed');
  assert.equal(r.json.failed.id, 'boot');
  assert.equal(r.json.failed.onFailure, 'stop');
  const base = git(app, 'rev-parse', 'HEAD');
  assert.equal(r.json.problems.length, 1);
  assert.ok(r.json.problems[0].startsWith(`check boot already fails on the base (${base.slice(0, 7)}) and does not say what failed, so a new failure in it could not be told from the old one. Fix it on the base branch, or, if you accept it for this build, run: dotnet "`), r.json.problems[0]);
  assert.ok(r.json.problems[0].endsWith(`" baseline approve --json --change ${CHANGE} --repo app --hook boot`), r.json.problems[0]);
  assert.equal(git(app, 'branch', '--show-current'), `change/${CHANGE}`);
});

test('after the approval, build.start passes and reuses the baseline', () => {
  const { app } = brokenOnMain([brokenCheck('boot', { ids: false })]);
  start(app);
  assert.equal(baseline(app, 'approve', '--hook', 'boot').status, 0);
  const r = start(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.details.baseline.reused, true);
  assert.deepEqual(r.json.details.baseline.approved, ['boot']);
});

test('an approval for one base commit does not count at another', () => {
  const { app } = brokenOnMain([brokenCheck('boot', { ids: false })]);
  start(app);
  baseline(app, 'approve', '--hook', 'boot');
  git(app, 'checkout', '-q', 'main');
  commit(app, { 'other.txt': 'x' }, 'chore: more on main');
  git(app, 'push', '-q', 'origin', 'main');
  git(app, 'checkout', '-q', `change/${CHANGE}`);
  git(app, 'reset', '-q', '--hard', 'main');
  const r = start(app);
  assert.equal(r.json.outcome, 'failed', r.stdout);
  assert.equal(r.json.failed.id, 'boot');
});

test('build.start --check takes no baseline', () => {
  const { master, app } = brokenOnMain([brokenCheck('boot', { ids: false })]);
  const r = start(app, '--check');
  assert.equal(r.json.outcome, 'passed', r.stdout + r.stderr);
  assert.ok(!existsSync(storeFile(master)), 'a check-only run took a baseline');
});

test('a build resumed with uncommitted files and no baseline yet defers it to build.finish', () => {
  const { app } = broken([brokenCheck('boot', { ids: false })]);
  writeFileSync(join(app, 'wip.txt'), 'work in progress');
  const r = start(app);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.match(r.json.details.baseline.deferred, /uncommitted files/);
  assert.equal(baseline(app, 'show').json.taken, false);
});

// build.finish against the baseline. A check that fails while broken.txt exists: its lines are the IDs, except that
// a file holding QUIET fails without saying what failed.
const judged = (id = 'boot', rest = {}) =>
  nodeHook(id, `const fs=require('fs'); if (fs.existsSync('broken.txt')) { const t=fs.readFileSync('broken.txt','utf8'); if (!t.includes('QUIET')) fs.writeFileSync(process.env.HOSHIN_FAILURES, t); process.exit(1) }`, rest);

/** A built workspace (plan and build logs in place) whose app base holds `onBase` and whose branch holds `now`. */
function builtWith(checks, { onBase, now }) {
  const w = workspace({ built: true, marker: hooksIn({ 'build.finish': checks }) });
  git(w.app, 'checkout', '-q', 'main');
  if (onBase !== undefined) commit(w.app, { 'broken.txt': onBase }, 'chore: base state');
  git(w.app, 'push', '-q', 'origin', 'main');
  git(w.app, 'checkout', '-q', `change/${CHANGE}`);
  git(w.app, 'reset', '-q', '--hard', 'main');
  if (now === null) {
    git(w.app, 'rm', '-q', 'broken.txt');
    commit(w.app, {}, 'fix(demo): remove broken');
  } else if (now !== undefined) commit(w.app, { 'broken.txt': now }, 'feat(demo): change broken');
  else commit(w.app, { 'work.txt': 'w' }, 'feat(demo): work');
  return w;
}

test('a check green on the base and red now blocks, as today', () => {
  const { app } = builtWith([judged()], { now: 'r1\n' });
  const r = finish(app);
  assert.equal(r.json.outcome, 'failed', r.stdout);
  assert.equal(r.json.failed.onFailure, 'fix');
});

test('a check red on the base and green now passes and is listed as fixed', () => {
  const { app } = builtWith([judged()], { onBase: 'r1\n', now: null });
  const r = finish(app);
  assert.equal(r.json.outcome, 'passed', r.stdout + r.stderr);
  assert.deepEqual(r.json.details.baseline.fixed, ['boot']);
});

test('a check red with the same IDs passes and is listed as still failing', () => {
  const { app } = builtWith([judged()], { onBase: 'r1\n' });
  const r = finish(app);
  assert.equal(r.json.outcome, 'passed', r.stdout + r.stderr);
  assert.deepEqual(r.json.details.baseline.stillFailing, [{ id: 'boot', ids: ['r1'] }]);
});

test('a check with a new ID blocks and names only the new one', () => {
  const { app } = builtWith([judged()], { onBase: 'r1\n', now: 'r1\nr2\n' });
  const r = finish(app);
  assert.equal(r.json.outcome, 'failed', r.stdout);
  assert.equal(r.json.failed.onFailure, 'fix');
  assert.match(r.json.problems.join('\n'), /r2/);
  assert.doesNotMatch(r.json.problems.join('\n'), /r1/);
});

test('a check that said what failed on the base and now does not blocks, as it cannot be compared', () => {
  const { app } = builtWith([judged()], { onBase: 'r1\n', now: 'QUIET\n' });
  const r = finish(app);
  assert.equal(r.json.outcome, 'failed', r.stdout);
  assert.match(r.json.problems.join('\n'), /could not be compared/);
});

test('a check red without IDs on the base passes only when approved', () => {
  const { app } = builtWith([judged()], { onBase: 'QUIET\n' });
  const blocked = finish(app);
  assert.equal(blocked.json.outcome, 'failed', blocked.stdout);
  assert.equal(blocked.json.failed.onFailure, 'stop');
  assert.match(blocked.json.problems.join('\n'), /baseline approve --json --change .* --hook boot/);
  assert.equal(baseline(app, 'approve', '--hook', 'boot').status, 0);
  // A plain rerun, as the skill does: only passed runs are reused, so no --again is needed.
  const r = finish(app);
  assert.equal(r.json.outcome, 'passed', r.stdout + r.stderr);
  assert.deepEqual(r.json.details.baseline.approved, ['boot']);
});

test('an allowed failure does not stop the next check, and a blocking one does', () => {
  const unit = nodeHook('unit', `process.exit(require('fs').existsSync('work.txt') ? 1 : 0)`);
  const allowedFirst = builtWith([judged(), unit], { onBase: 'r1\n' });
  const a = finish(allowedFirst.app);
  assert.deepEqual(a.json.hooks.map((h) => h.id), ['boot', 'unit']);
  assert.equal(a.json.failed.id, 'unit');
  const blockingFirst = builtWith([unit, judged()], { onBase: 'r1\n' });
  const b = finish(blockingFirst.app);
  assert.deepEqual(b.json.hooks.map((h) => h.id), ['unit']);
});

test('the Stop gate names the new IDs when build.finish failed on them', () => {
  const { app } = builtWith([judged()], { onBase: 'r1\n', now: 'r1\nr2\n' });
  assert.equal(start(app).json.outcome, 'passed');
  finish(app);
  const gate = spawnSync(dotnet, [program, 'gate'], { input: JSON.stringify({ cwd: app }), encoding: 'utf8', env });
  assert.match(JSON.parse(gate.stdout).reason, /r2/);
});

// The detached checkout of the base must always end back on the branch, at its commit, with nothing left over:
// otherwise build.finish would judge the base's tree and record the pass under the head's commit.
const status = (app) => git(app, 'status', '--porcelain');

test('a check that changes a tracked file on the base still leaves the repository on its branch, clean', () => {
  const touch = nodeHook('boot', `const fs=require('fs'); if (process.env.HOSHIN_BASELINE) fs.appendFileSync('broken.txt', 'touched'); process.exit(0)`);
  const { app } = broken([touch]);
  commit(app, { 'broken.txt': 'changed on the branch\n' }, 'feat(demo): change broken');
  const head = git(app, 'rev-parse', 'HEAD');
  const r = baseline(app, 'retake');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(git(app, 'branch', '--show-current'), `change/${CHANGE}`);
  assert.equal(git(app, 'rev-parse', 'HEAD'), head);
  assert.equal(status(app), '');
});

test('a check that leaves an untracked file the branch tracks still leaves the repository on its branch, clean', () => {
  const generate = nodeHook('boot', `const fs=require('fs'); if (process.env.HOSHIN_BASELINE) { fs.writeFileSync('gen.txt', 'generated'); fs.writeFileSync('stray.txt', 'x') } process.exit(0)`);
  const { app } = broken([generate]);
  commit(app, { 'gen.txt': 'tracked on the branch\n' }, 'feat(demo): add gen');
  const r = baseline(app, 'retake');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(git(app, 'branch', '--show-current'), `change/${CHANGE}`);
  assert.equal(status(app), '');
  assert.equal(readFileSync(join(app, 'gen.txt'), 'utf8'), 'tracked on the branch\n');
});

test('a baseline interrupted while detached is put back on its branch by the next run', () => {
  const { app } = builtWith([judged()], { onBase: 'r1\n' });
  const head = git(app, 'rev-parse', 'HEAD');
  const marker = join(git(app, 'rev-parse', '--path-format=absolute', '--git-dir'), 'hoshin', 'baseline-restore.json');
  mkdirSync(dirname(marker), { recursive: true });
  writeFileSync(marker, JSON.stringify({ branch: `change/${CHANGE}`, head }));
  git(app, 'checkout', '-q', '--detach', 'main');
  const r = finish(app);
  assert.equal(r.json.outcome, 'passed', r.stdout + r.stderr);
  assert.equal(git(app, 'branch', '--show-current'), `change/${CHANGE}`);
  assert.ok(!existsSync(marker), 'the marker stayed');
});

test('uncommitted files that refuse the baseline are named with what to do', () => {
  const { app } = broken([brokenCheck()]);
  writeFileSync(join(app, 'coverage.txt'), 'x');
  assert.match(baseline(app, 'retake').json.problems.join('\n'), /commit, ignore or remove/);
});
