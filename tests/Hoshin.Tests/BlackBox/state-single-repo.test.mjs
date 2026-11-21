// state, commit --repo and change show in the single-repo layout.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { buildLog, commit, git, run, write } from './helpers/workspace.mjs';
import { intent } from './helpers/documents.mjs';
import { singleRepo } from './helpers/single-repo.mjs';

const NAME = '2026-11-02-wave';
const DIR = `intent/demo/${NAME}`;
const task = (id, repo) =>
  `### ${id}: ${repo}\n\n\`\`\`jsonc\n{\n  "id": "${id}",\n  "repo": "${repo}",\n  "depends_on": []\n}\n\`\`\`\n\n#### Change\n\nChange it.\n\n#### Tests\n\nTest it first.\n`;
const PLAN = `\`\`\`jsonc\n{ "layout": 2, "system": "demo" }\n\`\`\`\n\n# Plan: Wave\n\n## Tasks\n\n${task('T1', 'api')}\n${task('T2', 'web')}`;

function started() {
  const s = singleRepo();
  const r = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', NAME, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Wave']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  const copy = r.json.details.copy;
  commit(copy, { [`${DIR}/intent.md`]: intent('draft'), [`${DIR}/plan.md`]: PLAN, [`${DIR}/build/api.md`]: buildLog(['T1']) }, 'docs(PROJ-1): plan');
  return { ...s, copy };
}

const work = (dir, file, taskId) => {
  write(join(dir, file), `${file}\n`);
  git(dir, 'add', '.');
  git(dir, 'commit', '-q', '-m', 'feat(PROJ-1): work', '-m', `Generated-By: hoshin\nPlan-Task: ${taskId}`);
  return git(dir, 'rev-parse', 'HEAD');
};

const changeOf = (s) => {
  const r = run(s.repo, ['state', '--json', '--system', 'demo']);
  assert.equal(r.status, 0, r.stderr);
  return r.json.changes.find((x) => x.change === NAME);
};

test('schedule: in the single-repo layout the apps are built one at a time', () => {
  const c = changeOf(started());
  assert.equal(c.schedule.oneAtATime, true);
  assert.deepEqual(c.schedule.repos.map((r) => [r.repo, r.state]), [['api', 'done'], ['web', 'ready']]);
});

test('state attributes a Plan-Task commit to the app whose folder it touches', () => {
  const s = started();
  const a = work(s.copy, 'apps/api/a.txt', 'T1');
  const w = work(s.copy, 'apps/web/w.txt', 'T2');
  const c = changeOf(s);
  assert.deepEqual(c.tasks.api.commits, { T1: [a] });
  assert.deepEqual(c.tasks.web.commits, { T2: [w] });
  assert.deepEqual(c.tasks.api.untasked, []);
  assert.deepEqual(c.tasks.api.uncommitted, []);
});

test("a commit that touches only another app is not the app's, and uncommitted files are per app", () => {
  const s = started();
  work(s.copy, 'apps/web/w.txt', 'T1');
  write(join(s.copy, 'apps', 'web', 'dirty.txt'), 'd\n');
  const c = changeOf(s);
  assert.deepEqual(c.tasks.api.commits, { T1: [] });
  assert.deepEqual(c.tasks.api.uncommitted, []);
  assert.deepEqual(c.tasks.web.uncommitted, ['apps/web/dirty.txt']);
});

test('commit --kind work --repo <app> commits in the copy', () => {
  const s = started();
  write(join(s.copy, 'apps', 'api', 'x.txt'), 'x\n');
  const r = run(s.copy, ['commit', '--json', '--kind', 'work', '--change', NAME, '--repo', 'api', '--task', 'T1', '--type', 'feat', '--subject', 'api x', '--', 'apps/api/x.txt']);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.committed, true);
  assert.equal(git(s.copy, 'log', '-1', '--format=%s'), r.json.header);
  assert.equal(git(s.repo, 'rev-parse', '--abbrev-ref', 'HEAD'), 'main');
});

test('change show gives each app its folder in the copy and the one repository branch and base', () => {
  const s = started();
  const r = run(s.repo, ['change', 'show', '--json', '--system', 'demo', '--change', NAME]);
  assert.equal(r.status, 0, r.stderr);
  const { repos } = r.json;
  assert.deepEqual(repos.api, { path: join(s.copy, 'apps', 'api'), checkedOut: true, branch: `change/${NAME}`, base: 'main' });
  assert.deepEqual(repos.web, { path: join(s.copy, 'apps', 'web'), checkedOut: true, branch: `change/${NAME}`, base: 'main' });
});
