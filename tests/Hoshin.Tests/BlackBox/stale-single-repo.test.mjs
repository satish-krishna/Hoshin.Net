// stale in the single-repo layout.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { commit, git, run } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';

const context = (name, sourceCommit) =>
  '```jsonc\n' + JSON.stringify({ repo: name, system: 'demo', source_commit: sourceCommit, built: '2026-10-20' }, null, 2) + '\n```\n\n# Context: ' + name + '\n';

function stale(s, ...args) {
  const r = run(s.repo, ['stale', '--json', '--system', 'demo', ...args]);
  assert.equal(r.status, 0, r.stderr);
  return r.json;
}

test('a commit in apps/web makes web stale and leaves api current', () => {
  const s = singleRepo();
  const first = git(s.repo, 'rev-parse', 'HEAD');
  commit(s.repo, { 'intent/demo/context/api.md': context('api', first), 'intent/demo/context/web.md': context('web', first) });
  commit(s.repo, { 'apps/web/a.txt': 'a\n' }, 'feat: web');
  const [api, web] = stale(s).repos;
  assert.deepEqual([api.repo, api.stale, api.behind, api.reason], ['api', false, 0, 'current']);
  assert.deepEqual([web.repo, web.stale, web.behind, web.reason], ['web', true, 1, 'behind']);
  assert.equal(web.path, join(s.repo, 'apps', 'web'));
  assert.equal(web.checkedOut, true);
  assert.equal(web.sourceCommit, first);
});

test('with one app at the root, a commit to intent/ leaves it current and a product commit makes it behind', () => {
  const s = singleRepo({ apps: { shop: '.' } });
  const first = git(s.repo, 'rev-parse', 'HEAD');
  commit(s.repo, { 'intent/demo/context/shop.md': context('shop', first) });
  commit(s.repo, { 'intent/demo/other/note.md': 'n\n' });
  let [shop] = stale(s).repos;
  assert.deepEqual([shop.stale, shop.behind, shop.reason], [false, 0, 'current']);
  assert.equal(shop.path, s.repo);
  commit(s.repo, { 'src.txt': 's\n' }, 'feat: product');
  [shop] = stale(s).repos;
  assert.deepEqual([shop.stale, shop.behind, shop.reason], [true, 1, 'behind']);
});

test('--repo selects an app', () => {
  const s = singleRepo();
  assert.deepEqual(stale(s, '--repo', 'web').repos.map((r) => r.repo), ['web']);
});
