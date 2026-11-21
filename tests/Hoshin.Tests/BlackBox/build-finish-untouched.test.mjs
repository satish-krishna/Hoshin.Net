// build.finish in the single-repo layout for an app the change did not touch.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { intent } from './helpers/documents.mjs';
import { buildLog, commit, git, nodeHook, run } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';

const NAME = '2026-11-02-wave';
const DIR = `intent/demo/${NAME}`;
const task = (id, repo) =>
  `### ${id}: ${repo}\n\n\`\`\`jsonc\n{\n  "id": "${id}",\n  "repo": "${repo}",\n  "depends_on": []\n}\n\`\`\`\n\n#### Change\n\nChange it.\n\n#### Tests\n\nTest it first.\n`;
const PLAN = `\`\`\`jsonc\n{ "layout": 2, "system": "demo" }\n\`\`\`\n\n# Plan: Wave\n\n## Tasks\n\n${task('T1', 'api')}\n${task('T2', 'web')}`;

function ready() {
  const s = singleRepo();
  const file = join(s.root, 'trace.txt');
  const hook = nodeHook('unit', `require('fs').appendFileSync(${JSON.stringify(file)}, require('path').basename(process.cwd()) + '\\n')`);
  const marker = JSON.parse(readFileSync(join(s.repo, '.hoshin.json'), 'utf8'));
  marker.hooks = { 'build.finish': [hook] };
  commit(s.repo, { '.hoshin.json': JSON.stringify(marker, null, 2) + '\n' }, 'chore: hooks');
  git(s.repo, 'push', '-q', 'origin', 'main');
  const r = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', NAME, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Wave']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  const copy = r.json.details.copy;
  commit(copy, { [`${DIR}/intent.md`]: intent('draft', { scope: 'PROJ-1' }), [`${DIR}/plan.md`]: PLAN, [`${DIR}/build/api.md`]: buildLog(['T1']), [`${DIR}/build/web.md`]: buildLog(['T2']) }, 'docs(PROJ-1): plan');
  commit(copy, { 'apps/api/a.txt': 'a' }, 'feat(PROJ-1): api');
  return { ...s, copy, file };
}
const finish = (s, app) => run(s.repo, ['run', 'build.finish', '--json', '--change', NAME, '--repo', app]);

test('an untouched app passes as a no-op: no hook, no baseline, and the result is in the ledger at its head', () => {
  const s = ready();
  const r = finish(s, 'web');
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  assert.deepEqual(r.json.details, { untouched: true });
  assert.deepEqual(r.json.hooks, []);
  assert.ok(!existsSync(s.file), 'a hook ran for web');
  const state = run(s.repo, ['state', '--json', '--system', 'demo']).json.changes.find((x) => x.change === NAME);
  const ledger = JSON.parse(readFileSync(join(s.repo, '.git', 'hoshin', 'demo', NAME, 'web.json'), 'utf8'));
  const last = ledger.runs[ledger.runs.length - 1];
  assert.equal(last.event, 'build.finish');
  assert.equal(last.outcome, 'passed');
  assert.equal(last.key, state.apps.web.head);
});

test('a touched app still runs its hooks', () => {
  const s = ready();
  const bs = run(s.repo, ['run', 'build.start', '--json', '--change', NAME, '--repo', 'api']);
  assert.equal(bs.json.outcome, 'passed', JSON.stringify(bs.json));
  const r = finish(s, 'api');
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  assert.notEqual(r.json.details.untouched, true);
  assert.ok(readFileSync(s.file, 'utf8').trim().split(/\r?\n/).every((f) => f === 'api'));
});
