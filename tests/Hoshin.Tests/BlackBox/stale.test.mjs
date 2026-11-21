// stale --json --system <s>
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { commit, git, repo, run, workspace, write } from './helpers/workspace.mjs';

const context = (name, sourceCommit) => {
  const fm = { repo: name, system: 'demo', ...(sourceCommit === undefined ? {} : { source_commit: sourceCommit }), built: '2026-10-20' };
  return '```jsonc\n' + JSON.stringify(fm, null, 2) + '\n```\n\n# Context: ' + name + '\n';
};
const contextPath = (master, name) => join(master, 'intent', 'demo', 'context', `${name}.md`);
const head = (dir) => git(dir, 'rev-parse', 'HEAD');

function stale(cwd, ...args) {
  const r = run(cwd, ['stale', '--json', '--system', 'demo', ...args]);
  assert.equal(r.status, 0, r.stderr);
  return r.json;
}

function ws(repos = ['app']) {
  return workspace({ marker: { systems: { demo: { repos } } } });
}

test('current: the context names HEAD; the entry carries the paths and facts', () => {
  const w = ws();
  commit(w.master, { 'intent/demo/context/app.md': context('app', head(w.app)) });
  const out = stale(w.master);
  assert.deepEqual(out, {
    system: 'demo',
    repos: [{ repo: 'app', path: w.app, checkedOut: true, context: contextPath(w.master, 'app'), exists: true, sourceCommit: head(w.app), behind: 0, stale: false, reason: 'current' }],
    problems: [],
  });
});

test('behind: commits after source_commit are counted, stale', () => {
  const w = ws();
  const first = head(w.app);
  commit(w.app, { 'a.txt': 'a\n' }, 'feat: a');
  commit(w.app, { 'b.txt': 'b\n' }, 'feat: b');
  commit(w.master, { 'intent/demo/context/app.md': context('app', first) });
  const [entry] = stale(w.master).repos;
  assert.equal(entry.behind, 2);
  assert.equal(entry.stale, true);
  assert.equal(entry.reason, 'behind');
});

test('missing: no context file, not stale', () => {
  const w = ws();
  const [entry] = stale(w.master).repos;
  assert.deepEqual(entry, { repo: 'app', path: w.app, checkedOut: true, context: contextPath(w.master, 'app'), exists: false, sourceCommit: null, behind: null, stale: false, reason: 'missing' });
});

test('the context file is read from the working tree, committed or not', () => {
  const w = ws();
  write(contextPath(w.master, 'app'), context('app', head(w.app)));
  const [entry] = stale(w.master).repos;
  assert.equal(entry.exists, true);
  assert.equal(entry.reason, 'current');
});

test('not-checked-out: the repository is not in the workspace, not stale', () => {
  const w = ws(['app', 'web']);
  commit(w.master, { 'intent/demo/context/app.md': context('app', head(w.app)), 'intent/demo/context/web.md': context('web', head(w.app)) });
  const out = stale(w.master);
  assert.deepEqual(out.repos.map((r) => r.repo), ['app', 'web']);
  const web = out.repos[1];
  assert.equal(web.path, join(w.ws, 'web'));
  assert.equal(web.checkedOut, false);
  assert.equal(web.exists, true);
  assert.equal(web.sourceCommit, head(w.app));
  assert.equal(web.behind, null);
  assert.equal(web.stale, false);
  assert.equal(web.reason, 'not-checked-out');
});

test('no-source-commit: no field, an empty one, or front matter that cannot be read; stale', () => {
  const w = ws();
  for (const text of [context('app'), context('app', ''), '```jsonc\n{ "repo": [ }\n```\n\n# Context\n', '```jsonc\n{ "repo": "app" }\n']) {
    write(contextPath(w.master, 'app'), text);
    const [entry] = stale(w.master).repos;
    assert.equal(entry.sourceCommit, null, text);
    assert.equal(entry.behind, null, text);
    assert.equal(entry.stale, true, text);
    assert.equal(entry.reason, 'no-source-commit', text);
  }
});

test('unknown-commit: a commit not in the repository (rewritten history), stale', () => {
  const w = ws();
  for (const sha of ['0123456789abcdef0123456789abcdef01234567', '--all', 'not-a-commit']) {
    write(contextPath(w.master, 'app'), context('app', sha));
    const [entry] = stale(w.master).repos;
    assert.equal(entry.sourceCommit, sha);
    assert.equal(entry.behind, null);
    assert.equal(entry.stale, true);
    assert.equal(entry.reason, 'unknown-commit');
  }
});

test('the master listed as a product repository: its own intent/ and marker commits do not count', () => {
  const w = ws(['app', 'master']);
  const pinned = head(w.master);
  commit(w.master, { 'intent/demo/context/master.md': context('master', pinned), 'intent/demo/context/app.md': context('app', head(w.app)) });
  commit(w.master, { '.hoshin.json': JSON.stringify({ version: 1, systems: { demo: { repos: ['app', 'master'] } } }, null, 2) + '\n\n' });
  let entry = stale(w.master).repos.find((r) => r.repo === 'master');
  assert.equal(entry.path, w.master);
  assert.equal(entry.behind, 0);
  assert.equal(entry.reason, 'current');
  commit(w.master, { 'src/tool.sh': 'echo\n' }, 'feat: tool');
  entry = stale(w.master).repos.find((r) => r.repo === 'master');
  assert.equal(entry.behind, 1);
  assert.equal(entry.reason, 'behind');
});

test('--repo narrows the output, in marker order; an unknown repository exits 2', () => {
  const w = ws(['app', 'web', 'api']);
  repo(join(w.ws, 'api'));
  assert.deepEqual(stale(w.master, '--repo', 'api', '--repo', 'app').repos.map((r) => r.repo), ['app', 'api']);
  const r = run(w.master, ['stale', '--json', '--system', 'demo', '--repo', 'nope']);
  assert.equal(r.status, 2);
  assert.match(r.stderr, /nope/);
});

test('an unknown system, no --system, or no --json exits 2', () => {
  const w = ws();
  assert.equal(run(w.master, ['stale', '--json', '--system', 'other']).status, 2);
  assert.equal(run(w.master, ['stale', '--json']).status, 2);
  assert.equal(run(w.master, ['stale', '--system', 'demo']).status, 2);
});

test('run from a product repository with --master, and from a copy, it reads the resolved master', () => {
  const w = ws();
  commit(w.master, { 'intent/demo/context/app.md': context('app', head(w.app)) });
  assert.equal(stale(w.app, '--master', w.master).repos[0].reason, 'current');
  const copy = join(w.ws, '.hoshin', 'copies', 'c1');
  git(w.master, 'worktree', 'add', '-q', '-b', 'change/c1', copy);
  commit(copy, { 'intent/demo/context/app.md': context('app') });
  const [entry] = stale(copy).repos;
  assert.equal(entry.context, contextPath(copy, 'app'));
  assert.equal(entry.reason, 'no-source-commit');
});
