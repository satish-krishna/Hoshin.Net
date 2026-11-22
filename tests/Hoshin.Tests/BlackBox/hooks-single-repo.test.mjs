// Hooks in the single-repo layout: the `apps` filter, the app folder as working directory, and the layout each filter belongs to.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, realpathSync } from 'node:fs';
import { join } from 'node:path';
import { nodeHook, run, workspace } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';
import { tempDir } from './helpers/temp.mjs';

const NAME = '2026-11-02-wave';

function scratch() {
  return realpathSync.native(tempDir('hoshin-hooks-'));
}

/** A hook that appends where it ran and what it was told to `out`. */
const recorder = (id, out, rest = {}) =>
  nodeHook(id, `require('fs').appendFileSync(${JSON.stringify(out)}, JSON.stringify({ id: ${JSON.stringify(id)}, cwd: process.cwd(), repo: process.env.HOSHIN_REPO, repoPath: process.env.HOSHIN_REPO_PATH, root: process.env.HOSHIN_ROOT }) + '\\n')`, rest);

const lines = (out) => (existsSync(out) ? readFileSync(out, 'utf8').trim().split('\n').map((l) => JSON.parse(l)) : []);

function started(hooks) {
  const s = singleRepo({ marker: { hooks } });
  const r = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', NAME, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Wave']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  return { ...s, copy: r.json.details.copy };
}

const taskDone = (s, app) => run(s.repo, ['run', 'task.done', '--json', '--change', NAME, '--repo', app, '--task', 'T1']);

test('a hook with apps runs for those apps only, and a hook without runs for every app', () => {
  const out = join(scratch(), 'out.jsonl');
  const s = started({ 'task.done': [recorder('web-only', out, { apps: ['web'] }), recorder('every', out)] });
  const web = taskDone(s, 'web');
  assert.equal(web.json.outcome, 'passed', JSON.stringify(web.json));
  assert.deepEqual(web.json.hooks.map((h) => h.id), ['web-only', 'every']);
  const api = taskDone(s, 'api');
  assert.equal(api.json.outcome, 'passed', JSON.stringify(api.json));
  assert.deepEqual(api.json.hooks.map((h) => h.id), ['every']);
  assert.deepEqual(lines(out).map((l) => [l.id, l.repo]), [['web-only', 'web'], ['every', 'web'], ['every', 'api']]);
});

test('a hook runs in the app folder of the copy, and HOSHIN_ROOT holds the repository root', () => {
  const out = join(scratch(), 'out.jsonl');
  const s = started({ 'task.done': [recorder('where', out)] });
  assert.equal(taskDone(s, 'api').json.outcome, 'passed');
  const [seen] = lines(out);
  assert.equal(realpathSync.native(seen.cwd), realpathSync.native(join(s.copy, 'apps', 'api')));
  assert.equal(seen.repoPath, join(s.copy, 'apps', 'api'));
  assert.equal(seen.root, s.copy);
});

test('in the default layout HOSHIN_ROOT equals HOSHIN_REPO_PATH', () => {
  const out = join(scratch(), 'out.jsonl');
  const w = workspace({ marker: { hooks: { 'build.start': [recorder('where', out)] } } });
  const r = run(w.master, ['run', 'build.start', '--json', '--change', '2026-11-01-greeting', '--repo', 'app']);
  assert.ok(lines(out).length > 0, JSON.stringify(r.json));
  const [seen] = lines(out);
  assert.equal(seen.root, seen.repoPath);
  assert.equal(seen.repo, 'app');
});

test('repos is a problem in the single-repo layout', () => {
  const s = singleRepo({ marker: { hooks: { 'task.done': [nodeHook('old', '0', { repos: ['web'] })] } } });
  const r = run(s.repo, ['hooks', '--json', '--system', 'demo']);
  assert.equal(r.status, 1);
  assert.match(r.json.problems.join('\n'), /\.hoshin\.json: hook old uses repos; use apps in the single-repo layout/);
});

test('apps is a problem in the default layout', () => {
  const w = workspace({ marker: { hooks: { 'build.finish': [nodeHook('new', '0', { apps: ['app'] })] } } });
  const r = run(w.master, ['hooks', '--json', '--system', 'demo']);
  assert.equal(r.status, 1);
  assert.match(r.json.problems.join('\n'), /\.hoshin\.json: hook new uses apps, which only the single-repo layout has/);
});

test('an app name the marker does not list is a problem naming it', () => {
  const s = singleRepo({ marker: { hooks: { 'task.done': [nodeHook('lost', '0', { apps: ['web', 'nope'] })] } } });
  const r = run(s.repo, ['hooks', '--json', '--system', 'demo']);
  assert.equal(r.status, 1);
  assert.match(r.json.problems.join('\n'), /hook lost: apps names nope, which is not an app of system demo/);
  assert.doesNotMatch(r.json.problems.join('\n'), /names web/);
});

test('check reports a layout mismatch in the marker, and accepts a valid apps filter', () => {
  const ok = singleRepo({ marker: { hooks: { 'task.done': [nodeHook('ok', '0', { apps: ['api'] })] } } });
  const good = run(ok.repo, ['check', join(ok.repo, '.hoshin.json')]);
  assert.equal(good.status, 0, good.stdout);
  const bad = singleRepo({ marker: { hooks: { 'task.done': [nodeHook('old', '0', { repos: ['api'] })] } } });
  const r = run(bad.repo, ['check', join(bad.repo, '.hoshin.json')]);
  assert.equal(r.status, 1);
  assert.match(r.stdout, /hook old uses repos; use apps in the single-repo layout/);
});

test('hooks --json prints apps for every hook, null when not set, after repos', () => {
  const s = singleRepo({ marker: { hooks: { 'task.done': [nodeHook('a', '0', { apps: ['api'] }), nodeHook('b', '0')] } } });
  const r = run(s.repo, ['hooks', '--json', '--system', 'demo']);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.json.hooks.map((h) => h.apps), [['api'], null]);
  assert.deepEqual(r.json.hooks.map((h) => h.repos), [null, null]);
  const keys = Object.keys(r.json.hooks[0]);
  assert.equal(keys.indexOf('apps'), keys.indexOf('repos') + 1);
  const w = workspace({ marker: { hooks: { 'build.finish': [nodeHook('c', '0')] } } });
  assert.equal(run(w.master, ['hooks', '--json', '--system', 'demo']).json.hooks[0].apps, null);
});
