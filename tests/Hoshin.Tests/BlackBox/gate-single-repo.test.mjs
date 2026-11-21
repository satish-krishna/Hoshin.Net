// The Stop gate and status in the single-repo layout.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { buildLog, commit, dotnet, env, git, program, run } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';

const NAME = '2026-11-02-wave';
const DIR = `intent/demo/${NAME}`;

const task = (id, repo) => `### ${id}: ${repo}

\`\`\`jsonc
{
  "id": "${id}",
  "repo": "${repo}",
  "depends_on": []
}
\`\`\`

#### Change

Change ${repo}.

#### Tests

Test it first.
`;
const PLAN = `\`\`\`jsonc
{
  "layout": 2,
  "system": "demo"
}
\`\`\`

# Plan: Wave

## Tasks

${task('T1', 'api')}
${task('T2', 'web')}`;

function started() {
  const s = singleRepo();
  const r = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', NAME, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Wave']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  const copy = r.json.details.copy;
  commit(copy, { [`${DIR}/plan.md`]: PLAN, [`${DIR}/build/api.md`]: buildLog(['T1']), [`${DIR}/build/web.md`]: buildLog(['T2']) }, 'docs(PROJ-1): plan');
  return { ...s, copy };
}

function gate(cwd) {
  const r = spawnSync(dotnet, [program, 'gate'], { input: JSON.stringify({ hook_event_name: 'Stop', cwd, stop_hook_active: false }), encoding: 'utf8', env });
  assert.equal(r.status, 0, r.stderr);
  return r.stdout.trim() === '' ? null : JSON.parse(r.stdout);
}

const buildStart = (s, app) => run(s.repo, ['run', 'build.start', '--json', '--change', NAME, '--repo', app]);
const buildFinish = (s, app) => run(s.repo, ['run', 'build.finish', '--json', '--change', NAME, '--repo', app]);

test('a commit outside the app, such as a build log entry, does not make a finished build block; a commit in the app does', () => {
  const s = started();
  const inApp = join(s.copy, 'apps', 'api');
  assert.equal(buildStart(s, 'api').json.outcome, 'passed');
  const blocked = gate(inApp);
  assert.equal(blocked?.decision, 'block', 'the checks have not run yet');
  assert.match(blocked.reason, /build\.finish --json --change 2026-11-02-wave --repo api --master/);
  assert.equal(buildFinish(s, 'api').json.outcome, 'passed');
  assert.equal(gate(inApp), null);
  commit(s.copy, { [`${DIR}/notes.md`]: 'x\n' }, 'docs(PROJ-1): note');
  commit(s.copy, { 'apps/web/more.txt': 'x\n' }, 'feat(PROJ-1): web');
  assert.equal(gate(inApp), null);
  assert.equal(gate(s.copy), null);
  commit(s.copy, { 'apps/api/more.txt': 'x\n' }, 'feat(PROJ-1): api');
  assert.equal(gate(inApp)?.decision, 'block');
});

test('status lists each app with its own head and whether its runs are current', () => {
  const s = started();
  buildStart(s, 'api');
  assert.equal(buildFinish(s, 'api').json.outcome, 'passed');
  buildStart(s, 'web');
  commit(s.copy, { 'apps/web/a.txt': 'x\n' }, 'feat(PROJ-1): web');
  assert.equal(buildFinish(s, 'web').json.outcome, 'passed');
  const r = run(s.repo, ['status', '--json', '--change', NAME]);
  assert.equal(r.status, 0, r.stderr);
  const byRepo = Object.fromEntries(r.json.repos.map((x) => [x.repo, x]));
  assert.ok(byRepo.api && byRepo.web);
  assert.notEqual(byRepo.api.head, byRepo.web.head);
  assert.equal(byRepo.api.head, git(s.copy, 'merge-base', 'main', 'HEAD'));
  assert.equal(byRepo.web.head, git(s.copy, 'log', '-1', '--format=%H', '--', 'apps/web'));
  assert.equal(byRepo.api.path, join(s.copy, 'apps', 'api'));
  const finishes = (x) => x.runs.filter((run) => run.event === 'build.finish');
  assert.ok(finishes(byRepo.api).every((x) => x.current));
  assert.ok(finishes(byRepo.web).every((x) => x.current));
  commit(s.copy, { 'apps/api/b.txt': 'x\n' }, 'feat(PROJ-1): api');
  const after = Object.fromEntries(run(s.repo, ['status', '--json', '--change', NAME]).json.repos.map((x) => [x.repo, x]));
  assert.ok(finishes(after.api).every((x) => !x.current), 'api moved');
  assert.ok(finishes(after.web).every((x) => x.current), 'web did not');
});
