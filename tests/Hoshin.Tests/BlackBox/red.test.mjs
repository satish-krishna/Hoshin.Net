// `red record`: the program, not a subagent's report, records whether a task's test failed at the commit that adds it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { CHANGE, git, run, workspace, write } from './helpers/workspace.mjs';
import { plan } from './helpers/documents.mjs';
import { singleRepo } from './helpers/single-repo.mjs';

const node = process.execPath;
const js = (code) => [node, '-e', code];

/** The accepted plan with a test (and compile) command on T1. */
function planWith(test, compile) {
  const extra = `,\n  "test": ${JSON.stringify(test)}` + (compile ? `,\n  "compile": ${JSON.stringify(compile)}` : '');
  return plan('accepted', 'abc1234').replace('"supports": []', `"supports": []${extra}`);
}

/**
 * A workspace whose master holds the accepted plan, with app on the change's branch and one commit
 * `<subject>` carrying `Plan-Task: T1` (or `trailer`).
 */
function setup({ test = js('process.exit(1)'), compile, subject = 'test(demo): greet', trailer = 'Plan-Task: T1', planText } = {}) {
  const text = planText ?? (test === null ? plan('accepted', 'abc1234') : planWith(test, compile));
  const w = workspace({ masterFiles: { [`intent/demo/${CHANGE}/plan.md`]: text } });
  git(w.app, 'checkout', '-q', '-b', `change/${CHANGE}`);
  write(join(w.app, 'greet.test.js'), 'x\n');
  git(w.app, 'add', '.');
  git(w.app, 'commit', '-q', '-m', subject, '-m', trailer);
  return { ...w, head: git(w.app, 'rev-parse', 'HEAD') };
}

const red = (w, extraEnv = {}, task = 'T1') => run(w.app, ['red', 'record', '--json', '--change', CHANGE, '--repo', 'app', '--task', task], extraEnv);
const ledgerFile = (w) => join(git(w.master, 'rev-parse', '--path-format=absolute', '--git-common-dir'), 'hoshin', 'demo', CHANGE, 'app.json');
const reds = (w) => (existsSync(ledgerFile(w)) ? (JSON.parse(readFileSync(ledgerFile(w), 'utf8')).reds ?? []) : []);

test('records red when the test fails at the test commit', () => {
  const test = js('process.exit(1)');
  const compile = js('process.exit(0)');
  const w = setup({ test, compile });
  const r = red(w);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.outcome, 'failed');
  assert.equal(r.json.commit, w.head);
  assert.equal(r.json.exitCode, 1);
  assert.equal(r.json.task, 'T1');
  assert.deepEqual(r.json.problems, []);
  const [rec, ...more] = reds(w);
  assert.equal(more.length, 0);
  assert.equal(rec.commit, w.head);
  assert.equal(rec.task, 'T1');
  assert.deepEqual(rec.test, test);
  assert.deepEqual(rec.compile, compile);
  assert.equal(rec.outcome, 'failed');
  assert.ok(!Number.isNaN(Date.parse(rec.at)));
});

test('a compile failure is not red, and the test never runs', () => {
  const marker = join(process.cwd(), `red-marker-${process.pid}.txt`);
  const w = setup({ test: js(`require('fs').writeFileSync(${JSON.stringify(marker)}, 'ran'); process.exit(1)`), compile: js('process.exit(2)') });
  const r = red(w);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.json.outcome, 'could-not-compile');
  assert.equal(r.json.exitCode, 2);
  assert.equal(existsSync(marker), false);
  assert.equal(reds(w).length, 1);
  assert.equal(reds(w)[0].outcome, 'could-not-compile');
});

test('reports a test that passes at the test commit', () => {
  const w = setup({ test: js('process.exit(0)') });
  const r = red(w);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.json.outcome, 'passed');
  assert.equal(reds(w)[0].outcome, 'passed');
  assert.equal(reds(w)[0].compile, null);
});

test('reads the commands from the plan in HEAD, not the working tree', () => {
  const w = setup({ test: js('process.exit(1)') });
  writeFileSync(join(w.master, 'intent', 'demo', CHANGE, 'plan.md'), planWith(js('process.exit(0)')));
  const r = red(w);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.outcome, 'failed');
});

test('refuses a task with no test command', () => {
  const w = setup({ test: null });
  const r = red(w);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, ["T1 has no test command in the accepted plan; the plan skill names it in the task's test field"]);
  assert.deepEqual(reds(w), []);
});

test('refuses a HEAD that is not a commit of the task', () => {
  const w = setup({ trailer: 'Plan-Task: T2' });
  const r = red(w);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, [`HEAD (${w.head.slice(0, 7)}) is not a commit of T1: red is recorded on the commit that adds T1's failing test`]);
  assert.deepEqual(reds(w), []);
});

test('refuses a HEAD that is not a test commit', () => {
  const w = setup({ subject: 'feat(demo): greet' });
  const r = red(w);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, [`HEAD (${w.head.slice(0, 7)}) is a feat commit: red is recorded on the commit that adds T1's failing test, before any production code`]);
  assert.deepEqual(reds(w), []);
});

test('refuses a test commit made after production commits, and the earlier red stays the verdict', () => {
  const w = setup();
  assert.equal(red(w).status, 0);
  const prod = commitFiles(w, { 'greet.js': 'g\n' }, 'feat(demo): greet');
  commitFiles(w, { 'greet.test.js': 'stronger\n' }, 'test(demo): strengthen greet');
  const r = red(w);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, [`T1 already has production commits before HEAD (${prod.slice(0, 7)}): red is recorded only before the task's first production commit`]);
  assert.equal(r.json.outcome, null);
  assert.equal(reds(w).length, 1);
  const s = show(w);
  assert.equal(s.json.observed, true);
  assert.equal(s.json.record.commit, w.head);
});

test('refuses a repository with uncommitted files', () => {
  const w = setup();
  write(join(w.app, 'b.txt'), 'b\n');
  write(join(w.app, 'a.txt'), 'a\n');
  const r = red(w);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, ['app has uncommitted files: a.txt, b.txt; red is recorded on a clean test commit']);
  assert.deepEqual(reds(w), []);
});

test('stops a command at its timeout', () => {
  const w = setup({ test: js('setTimeout(() => {}, 60000)') });
  const r = red(w, { HOSHIN_RED_TIMEOUT: '1' });
  assert.equal(r.status, 2, r.stdout + r.stderr);
  assert.equal(r.json.outcome, 'could-not-run');
  assert.equal(reds(w)[0].outcome, 'could-not-run');
  assert.equal(reds(w)[0].exitCode, null);
});

test('refuses a task that is not in the plan, and bad usage exits 2', () => {
  const w = setup();
  const r = red(w, {}, 'T9');
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, ['T9 is not a task of the plan committed in HEAD of the master repository']);
  assert.equal(run(w.app, ['red', 'record', '--json', '--change', CHANGE, '--repo', 'app']).status, 2);
  assert.equal(run(w.app, ['red', 'record', '--json', '--change', CHANGE, '--repo', 'app', '--task', 'x']).status, 2);
});

/**
 * A single-repo workspace whose change copy holds the accepted plan (T1 in app `api`, whose test command exits 0 in
 * the app's folder and 7 anywhere else) and one `test(demo): greet` commit with `Plan-Task: T1` touching `folder`.
 */
function singleRepoTestCommit(folder) {
  const s = singleRepo();
  const started = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', CHANGE, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Greeting']);
  assert.equal(started.json.outcome, 'passed', JSON.stringify(started.json));
  const copy = started.json.details.copy;
  const test = js("process.exit(require('path').basename(process.cwd()) === 'api' ? 0 : 7)");
  const text = plan('accepted', 'abc1234', [['T1', 'api']]).replace('"supports": []', `"supports": [],\n  "test": ${JSON.stringify(test)}`);
  write(join(copy, 'intent', 'demo', CHANGE, 'plan.md'), text);
  git(copy, 'add', '.');
  git(copy, 'commit', '-q', '-m', 'docs(demo): accept the plan');
  write(join(copy, folder, 'greet.test.js'), 'x\n');
  git(copy, 'add', '.');
  git(copy, 'commit', '-q', '-m', 'test(demo): greet', '-m', 'Plan-Task: T1');
  const redApi = () => run(s.repo, ['red', 'record', '--json', '--change', CHANGE, '--repo', 'api', '--task', 'T1']);
  const ledger = () => {
    const file = join(git(s.repo, 'rev-parse', '--path-format=absolute', '--git-common-dir'), 'hoshin', 'demo', CHANGE, 'api.json');
    return existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : null;
  };
  return { head: git(copy, 'rev-parse', 'HEAD'), redApi, ledger };
}

test('runs the test in the app folder in the single-repo layout', () => {
  const { head, redApi, ledger } = singleRepoTestCommit('apps/api');
  const r = redApi();
  // The command exits 0 only in apps/api, so a run anywhere else would exit 7: the test passing proves where it ran.
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.json.outcome, 'passed');
  assert.equal(r.json.exitCode, 0);
  assert.doesNotMatch(r.json.output, /SyntaxError/);
  assert.equal(r.json.commit, head);
  const reds = ledger().reds;
  assert.equal(reds.length, 1);
  assert.equal(reds[0].commit, head);
  assert.equal(reds[0].exitCode, 0);
});

test('a test commit that touches only another app is not a commit of the task', () => {
  const { head, redApi, ledger } = singleRepoTestCommit('apps/web');
  const r = redApi();
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.deepEqual(r.json.problems, [`HEAD (${head.slice(0, 7)}) is not a commit of T1: red is recorded on the commit that adds T1's failing test`]);
  assert.equal(ledger(), null);
});

// `red show`: the verdict the build coordinator copies into the build log.
const show = (w, task = 'T1') => run(w.app, ['red', 'show', '--json', '--change', CHANGE, '--repo', 'app', '--task', task]);
const sha7 = (sha) => sha.slice(0, 7);
/** Commits `files` (name -> content) in app with the subject and trailer, and returns the new SHA. */
function commitFiles(w, files, subject, trailer = 'Plan-Task: T1') {
  for (const [name, content] of Object.entries(files)) write(join(w.app, name), content);
  git(w.app, 'add', '.');
  git(w.app, 'commit', '-q', '-m', subject, '-m', trailer);
  return git(w.app, 'rev-parse', 'HEAD');
}

test('show: no record is not observed', () => {
  const w = setup();
  const r = show(w);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.task, 'T1');
  assert.equal(r.json.observed, false);
  assert.equal(r.json.reason, 'no red recorded for T1 on this machine');
  assert.equal(r.json.record, null);
  assert.deepEqual(r.json.problems, []);
});

test('show: a record at a commit that is no longer on the branch is not observed', () => {
  const w = setup();
  assert.equal(red(w).status, 0);
  git(w.app, 'commit', '-q', '--amend', '-m', 'test(demo): greet again', '-m', 'Plan-Task: T1');
  const r = show(w);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.observed, false);
  assert.equal(r.json.reason, `red was recorded at ${sha7(w.head)}, which is not a commit of T1 on this branch`);
  assert.equal(r.json.record.commit, w.head);
});

test('show: a test that passed is not observed', () => {
  const w = setup({ test: js('process.exit(0)') });
  assert.equal(red(w).status, 1);
  const r = show(w);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.observed, false);
  assert.equal(r.json.reason, `the test passed at ${sha7(w.head)}, before the production code`);
  assert.equal(r.json.record.outcome, 'passed');
});

test('show: a test that did not compile is not observed', () => {
  const w = setup({ compile: js('process.exit(2)') });
  assert.equal(red(w).status, 1);
  const r = show(w);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.observed, false);
  assert.equal(r.json.reason, `the test did not compile at ${sha7(w.head)}`);
});

test('show: a test that could not run is not observed', () => {
  const w = setup({ test: js('setTimeout(() => {}, 60000)') });
  assert.equal(red(w, { HOSHIN_RED_TIMEOUT: '1' }).status, 2);
  const r = show(w);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.observed, false);
  assert.equal(r.json.reason, `the test could not run at ${sha7(w.head)}`);
});

test('show: later commits of the task, even ones that change the test file, keep red observed', () => {
  const w = setup();
  assert.equal(red(w).status, 0);
  commitFiles(w, { 'greet.js': 'g\n', 'greet.test.js': 'filled in with the stub\n' }, 'feat(demo): greet');
  const r = show(w);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.observed, true);
  assert.equal(r.json.reason, null);
  assert.equal(r.json.record.commit, w.head);
  assert.equal(r.json.record.outcome, 'failed');
});

test('show: the newest record decides', () => {
  // compile exits 0 only once stub.js exists, so the first test commit does not compile and the second is red.
  const w = setup({ compile: js("process.exit(require('fs').existsSync('stub.js') ? 0 : 2)") });
  assert.equal(red(w).status, 1);
  assert.equal(show(w).json.reason, `the test did not compile at ${sha7(w.head)}`);
  const second = commitFiles(w, { 'stub.js': 's\n', 'greet2.test.js': 'y\n' }, 'test(demo): greet more');
  assert.equal(red(w).status, 0);
  assert.equal(reds(w).length, 2);
  const r = show(w);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.observed, true);
  assert.equal(r.json.reason, null);
  assert.equal(r.json.record.commit, second);
});

test('show: bad usage exits 2', () => {
  const w = setup();
  assert.equal(run(w.app, ['red', 'show', '--json', '--change', CHANGE, '--repo', 'app']).status, 2);
  assert.equal(run(w.app, ['red', 'show', '--json', '--change', CHANGE, '--repo', 'app', '--task', 'x']).status, 2);
  assert.equal(run(w.app, ['red', 'show', '--change', CHANGE, '--repo', 'app', '--task', 'T1']).status, 2);
});
