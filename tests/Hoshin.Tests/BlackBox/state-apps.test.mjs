// state --json lists a change's apps in the single-repo layout with their folder, whether the change touched them and
// their head, and leaves the field out in the default layout.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { commit, git, run, workspace } from './helpers/workspace.mjs';
import { intent } from './helpers/documents.mjs';
import { singleRepo } from './helpers/single-repo.mjs';

const NAME = '2026-11-02-wave';
const DIR = `intent/demo/${NAME}`;

function started(opts) {
  const s = singleRepo(opts);
  const r = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', NAME, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Wave']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  const copy = r.json.details.copy;
  commit(copy, { [`${DIR}/intent.md`]: intent('draft') }, 'docs(PROJ-1): intent');
  return { ...s, copy };
}
const changeOf = (s) => {
  const r = run(s.repo, ['state', '--json', '--system', 'demo']);
  assert.equal(r.status, 0, r.stderr);
  return r.json.changes.find((x) => x.change === NAME);
};

test('apps are keyed in marker order with folder, touched and head; an untouched app heads at the merge base', () => {
  const s = started();
  const base = git(s.copy, 'merge-base', 'main', 'HEAD');
  const api = commit(s.copy, { 'apps/api/a.txt': 'a' }, 'feat(PROJ-1): api');
  commit(s.copy, { [`${DIR}/build/review.md`]: 'x' }, 'docs(PROJ-1): later document commit');
  const c = changeOf(s);
  assert.deepEqual(Object.keys(c.apps), ['api', 'web']);
  assert.deepEqual(c.apps.api, { folder: 'apps/api', touched: true, head: api });
  assert.deepEqual(c.apps.web, { folder: 'apps/web', touched: false, head: base });
});

test('an app at . whose only commits are under intent/ is untouched and heads at the merge base', () => {
  const s = started({ apps: { shop: '.' } });
  const base = git(s.copy, 'merge-base', 'main', 'HEAD');
  const c = changeOf(s);
  assert.deepEqual(c.apps, { shop: { folder: '.', touched: false, head: base } });
  const code = commit(s.copy, { 'src.txt': 'x' }, 'feat(PROJ-1): code');
  assert.deepEqual(changeOf(s).apps.shop, { folder: '.', touched: true, head: code });
});

test('the default layout has no apps field', () => {
  const w = workspace({ built: false });
  const r = run(w.master, ['state', '--json', '--system', 'demo']);
  for (const c of r.json?.changes ?? []) assert.ok(!('apps' in c));
});

test('a change whose base cannot be resolved reports its apps untouched with a null head and a problem, not a failed state', () => {
  const s = started();
  commit(s.copy, { 'apps/api/a.txt': 'a' }, 'feat(PROJ-1): api');
  s.git('checkout', '-q', '-b', 'parked');
  s.git('branch', '-D', 'main');
  s.git('update-ref', '-d', 'refs/remotes/origin/main');
  const r = run(s.repo, ['state', '--json', '--system', 'demo']);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  const c = r.json.changes.find((x) => x.change === NAME);
  assert.deepEqual(c.apps, { api: { folder: 'apps/api', touched: false, head: null }, web: { folder: 'apps/web', touched: false, head: null } });
  assert.ok(r.json.problems.some((p) => p.startsWith(`${NAME}: `) && /no common commit/.test(p)), JSON.stringify(r.json.problems));
});
