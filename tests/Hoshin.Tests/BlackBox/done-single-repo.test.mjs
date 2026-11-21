// change.done in the single-repo layout.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { chmodSync, existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildLog, commit, git, nodeHook, run, write } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';
import { tempDir } from './helpers/temp.mjs';

const FAKE_GH = `#!/usr/bin/env node
const fs = require('fs'), path = require('path');
const args = process.argv.slice(2), repo = path.basename(process.cwd());
const stateFile = process.env.FAKE_GH_STATE;
const state = fs.existsSync(stateFile) ? JSON.parse(fs.readFileSync(stateFile, 'utf8')) : { prs: [] };
fs.appendFileSync(process.env.FAKE_GH_LOG, JSON.stringify({ repo, args }) + '\\n');
const opt = (name) => args[args.indexOf(name) + 1];
if (args[0] === 'auth') process.exit(0);
if (args[0] === 'pr' && args[1] === 'list') {
  const found = state.prs.filter((p) => p.repo === repo && p.head === opt('--head')).map(({ number, url }) => ({ number, url }));
  console.log(JSON.stringify(found.slice(0, 1)));
} else if (args[0] === 'pr' && args[1] === 'create') {
  const number = state.prs.length + 1, url = 'https://github.com/example/' + repo + '/pull/' + number;
  state.prs.push({ repo, number, url, head: opt('--head'), base: opt('--base'), title: opt('--title'), body: opt('--body') });
  fs.writeFileSync(stateFile, JSON.stringify(state));
  console.log(url);
} else if (args[0] === 'pr' && args[1] === 'view') {
  console.log(JSON.stringify({ state: 'OPEN' }));
} else process.exit(3);
`;

function fakeGitHub() {
  const dir = tempDir('hoshin-gh-');
  writeFileSync(join(dir, 'gh.cjs'), FAKE_GH);
  const env = { HOSHIN_GH: JSON.stringify([process.execPath, join(dir, 'gh.cjs')]), FAKE_GH_STATE: join(dir, 'state.json'), FAKE_GH_LOG: join(dir, 'log.jsonl') };
  return {
    env,
    dir,
    calls: () => (existsSync(env.FAKE_GH_LOG) ? readFileSync(env.FAKE_GH_LOG, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []),
    prs: () => (existsSync(env.FAKE_GH_STATE) ? JSON.parse(readFileSync(env.FAKE_GH_STATE, 'utf8')).prs : []),
  };
}

const NAME = '2026-11-02-wave';
const DIR = `intent/demo/${NAME}`;

const PLAN = `\`\`\`jsonc
{
  "layout": 2,
  "system": "demo"
}
\`\`\`

# Plan: Wave

## Tasks

### T1: Api

\`\`\`jsonc
{
  "id": "T1",
  "repo": "api",
  "depends_on": []
}
\`\`\`

#### Change

Change the api.

#### Tests

Test it first.

### T2: Web

\`\`\`jsonc
{
  "id": "T2",
  "repo": "web",
  "depends_on": []
}
\`\`\`

#### Change

Change the web app.

#### Tests

Test it first.
`;

const review = (commits) =>
  '```jsonc\n{ "layout": 2 }\n```\n\n# Review findings\n\n## Review 1\n\n```jsonc\n' +
  JSON.stringify({ review: 1, reviewed_at: '2026-11-01', commits: Object.fromEntries(Object.entries(commits).map(([k, v]) => [k, v.slice(0, 7)])) }, null, 2) +
  '\n```\n';

function ready() {
  const gh = fakeGitHub();
  const trace = join(gh.dir, 'trace.txt');
  const hook = nodeHook('unit', `require('fs').appendFileSync(${JSON.stringify(trace)}, require('path').basename(process.cwd()) + '\\n')`);
  const s = singleRepo({ marker: { hooks: { 'build.finish': [hook] } } });
  const r = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', NAME, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Wave']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  const copy = r.json.details.copy;
  commit(copy, { [`${DIR}/plan.md`]: PLAN, [`${DIR}/build/api.md`]: buildLog(['T1']), [`${DIR}/build/web.md`]: buildLog(['T2']) }, 'docs(PROJ-1): plan');
  assert.equal(run(s.repo, ['run', 'build.start', '--json', '--change', NAME, '--repo', 'api']).json.outcome, 'passed');
  const head = commit(copy, { 'apps/api/wave.txt': 'o/' }, 'feat(PROJ-1): wave');
  assert.equal(run(s.repo, ['run', 'build.finish', '--json', '--change', NAME, '--repo', 'api']).json.outcome, 'passed');
  commit(copy, { [`${DIR}/build/review.md`]: review({ api: head }) }, `docs(PROJ-1): record review 1 of ${NAME}`);
  return { ...s, copy, head, gh, trace };
}

const done = (w, extra = {}) => run(w.repo, ['run', 'change.done', '--json', '--change', NAME], { ...w.gh.env, ...extra });
const remoteHead = (w) => git(w.copy, 'ls-remote', '--heads', 'origin', `change/${NAME}`).split(/\s/)[0];
const traced = (w) => (existsSync(w.trace) ? readFileSync(w.trace, 'utf8').split('\n').filter(Boolean) : []);

test('with two apps and only api touched, done pushes once, opens one pull request and runs build.finish for api only', () => {
  const w = ready();
  const r = done(w);
  assert.equal(r.json.outcome, 'passed', r.stderr + r.stdout);
  assert.ok(traced(w).includes('api'), 'build.finish never ran for api: ' + traced(w));
  assert.ok(traced(w).every((folder) => folder === 'api'), 'build.finish ran for an app it should not have: ' + traced(w));
  assert.deepEqual(r.json.details.pushed, ['master']);
  assert.deepEqual(r.json.details.untouched, ['web']);
  assert.equal(w.gh.calls().filter((c) => c.args[0] === 'pr' && c.args[1] === 'create').length, 1);
  const prs = w.gh.prs();
  assert.equal(prs.length, 1);
  assert.equal(prs[0].head, `change/${NAME}`);
  assert.equal(prs[0].body, `Change \`${NAME}\` of system \`demo\`, built with hoshin. Its intent, spec, plan, build logs and review are in this pull request, under \`intent/demo/${NAME}/\`.\n\nApps touched: \`api\`.`);
  assert.equal(remoteHead(w), git(w.copy, 'rev-parse', 'HEAD'), 'the record commit was not pushed');
  const recordText = readFileSync(join(w.copy, DIR, 'change.jsonc'), 'utf8');
  const record = JSON.parse(recordText.replace(/^\/\/.*\n/gm, ''));
  assert.equal(record.pullRequests.length, 1);
  assert.equal(record.pullRequests[0].repo, 'master');
  assert.match(record.pullRequests[0].url, /^https:\/\/github\.com\/example\/[^"]+\/pull\/1$/);
  assert.equal(git(w.copy, 'status', '--porcelain'), '');
});

test('the review commit made after the reviewed SHAs does not make the review stale', () => {
  const w = ready();
  assert.notEqual(git(w.copy, 'rev-parse', 'HEAD'), w.head);
  const r = done(w);
  assert.equal(r.json.outcome, 'passed', r.stderr + r.stdout);
});

test('a later commit to a touched app makes the review stale and nothing is pushed', () => {
  const w = ready();
  commit(w.copy, { 'apps/api/later.txt': 'x' }, 'fix(PROJ-1): later');
  const r = done(w);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems.join('\n'), /the latest review covered api at [0-9a-f]{7}, but its branch is now at/);
  assert.equal(remoteHead(w), '');
  assert.deepEqual(w.gh.prs(), []);
});

test('a touched app the plan does not name must be covered by the review', () => {
  const w = ready();
  commit(w.copy, { 'apps/web/extra.txt': 'x' }, 'feat(PROJ-1): web too');
  const r = done(w);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems.join('\n'), /the latest review covered web at no commit/);
});

test('a plan app without its tasks logged, or uncommitted files in the copy, refuse done before gh is called', () => {
  const w = ready();
  rmSync(join(w.copy, DIR, 'build', 'web.md'));
  git(w.copy, 'add', '-A');
  git(w.copy, 'commit', '-q', '-m', 'docs(PROJ-1): drop log', '-m', 'Generated-By: hoshin');
  write(join(w.copy, 'stray.txt'), 'x');
  const r = done(w);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems.join('\n'), /web still has tasks without a build log entry: T2/);
  assert.match(r.json.problems.join('\n'), /uncommitted files: stray\.txt/);
  assert.equal(w.gh.calls().length, 0);
});

test('a rerun after a failed push carries on and opens the one pull request', () => {
  const w = ready();
  const hook = join(w.repo, '.git', 'hooks', 'pre-push');
  write(hook, '#!/bin/sh\necho "no pushing today" >&2\nexit 1\n');
  chmodSync(hook, 0o755);
  const failed = done(w);
  assert.equal(failed.json.outcome, 'failed');
  assert.match(failed.json.problems[0], /pushing change\/2026-11-02-wave in master failed .*no pushing today/s);
  assert.match(failed.json.problems[1], /nothing was pushed/);
  assert.deepEqual(w.gh.prs(), []);
  rmSync(hook);
  const again = done(w);
  assert.equal(again.json.outcome, 'passed', again.stderr + again.stdout);
  assert.equal(w.gh.prs().length, 1);
  assert.equal(remoteHead(w), git(w.copy, 'rev-parse', 'HEAD'));
  const third = done(w);
  assert.equal(third.json.outcome, 'passed', third.stderr + third.stdout);
  assert.equal(w.gh.prs().length, 1);
});

test('a failing build.finish for a touched app stops done before anything is pushed', () => {
  const w = ready();
  const marker = JSON.parse(readFileSync(join(w.repo, '.hoshin.json'), 'utf8'));
  marker.hooks['build.finish'] = [nodeHook('unit', 'console.log("2 tests failed"); process.exitCode = 1')];
  commit(w.repo, { '.hoshin.json': JSON.stringify(marker, null, 2) + '\n' }, 'chore: failing check');
  git(w.repo, 'push', '-q', 'origin', 'main');
  commit(w.copy, { 'apps/api/more.txt': 'x' }, 'feat(PROJ-1): more');
  const head = git(w.copy, 'log', '-1', '--format=%H', '--', 'apps/api');
  commit(w.copy, { [`${DIR}/build/review.md`]: review({ api: head }) }, `docs(PROJ-1): record review 1 of ${NAME}`);
  const r = done(w);
  assert.equal(r.json.outcome, 'refused', r.stderr + r.stdout);
  assert.match(r.json.problems.join('\n'), /api's checks \(build\.finish\) did not pass/);
  assert.equal(remoteHead(w), '');
});
