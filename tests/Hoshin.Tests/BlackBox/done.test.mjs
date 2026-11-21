// Declaring a change done: change.done verifies the change, pushes every touched repository, opens its pull requests
// and records them in change.jsonc.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { chmodSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildLog, CHANGE, commit, git, INTENT, nodeHook, run, testsRoot, workspace, write } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const FAKE_GH = `#!/usr/bin/env node
const fs = require('fs'), path = require('path');
const args = process.argv.slice(2), repo = path.basename(process.cwd());
const stateFile = process.env.FAKE_GH_STATE;
const state = fs.existsSync(stateFile) ? JSON.parse(fs.readFileSync(stateFile, 'utf8')) : { prs: [] };
fs.appendFileSync(process.env.FAKE_GH_LOG, JSON.stringify({ repo, args }) + '\\n');
const opt = (name) => args[args.indexOf(name) + 1];
if (args[0] === 'auth') process.exit(process.env.FAKE_GH_AUTH === 'fail' ? 1 : 0);
if (args[0] === 'pr' && args[1] === 'list') {
  const found = state.prs.filter((p) => p.repo === repo && p.head === opt('--head')).map(({ number, url }) => ({ number, url }));
  console.log(JSON.stringify(found.slice(0, 1)));
} else if (args[0] === 'pr' && args[1] === 'create') {
  if (process.env.FAKE_GH_CREATE === 'fail') { console.error('GraphQL: something went wrong'); process.exit(1); }
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
    calls: () => (existsSync(env.FAKE_GH_LOG) ? readFileSync(env.FAKE_GH_LOG, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []),
    prs: () => (existsSync(env.FAKE_GH_STATE) ? JSON.parse(readFileSync(env.FAKE_GH_STATE, 'utf8')).prs : []),
  };
}

const review = (head) =>
  '```jsonc\n{ "layout": 2 }\n```\n\n# Review findings\n\n## Review 1\n\n```jsonc\n' +
  JSON.stringify({ review: 1, reviewed_at: '2026-11-01', commits: { app: head.slice(0, 7) } }, null, 2) +
  '\n```\n';

function ready(options = {}) {
  const w = workspace({ built: true, marker: { hooks: { 'build.finish': [nodeHook('unit', '0')] }, ...(options.marker ?? {}) } });
  git(w.master, 'checkout', '-q', '-b', `change/${CHANGE}`);
  const head = commit(w.app, { 'hello.txt': 'hi' }, 'feat(demo): say hello');
  commit(w.master, { [`intent/demo/${CHANGE}/build/review.md`]: review(head) }, `docs(demo): record review 1 of ${CHANGE}`);
  return { ...w, head, gh: fakeGitHub() };
}

const done = (w, extra = {}) => run(w.master, ['run', 'change.done', '--json', '--change', CHANGE], { ...w.gh.env, ...extra });
const remoteHas = (path, branch) => git(path, 'ls-remote', '--heads', 'origin', branch) !== '';

test('done pushes app then the master, opens a pull request for each, and records them in change.jsonc', () => {
  const w = ready();
  const r = done(w);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.outcome, 'passed');
  assert.deepEqual(r.json.details.pushed, ['app', 'master']);
  const prs = w.gh.prs();
  assert.deepEqual(prs.map((p) => [p.repo, p.head, p.base, p.title]), [['app', `change/${CHANGE}`, 'main', 'Greeting'], ['master', `change/${CHANGE}`, 'main', 'Greeting']]);
  assert.match(prs[1].body, /- app: https:\/\/github\.com\/example\/app\/pull\/1/);
  assert.equal(git(w.app, 'ls-remote', '--heads', 'origin', `change/${CHANGE}`).split(/\s/)[0], w.head);
  assert.equal(git(w.master, 'ls-remote', '--heads', 'origin', `change/${CHANGE}`).split(/\s/)[0], git(w.master, 'rev-parse', 'HEAD'));
  const recordText = readFileSync(join(w.master, 'intent/demo', CHANGE, 'change.jsonc'), 'utf8');
  const record = JSON.parse(recordText.replace(/^\/\/.*\n/gm, ''));
  assert.equal(record.pullRequests.length, 2);
  assert.equal(record.pullRequests[0].repo, 'app');
  assert.equal(record.pullRequests[0].url, 'https://github.com/example/app/pull/1');
  assert.equal(record.pullRequests[0].number, 1);
  assert.match(record.pullRequests[0].pushed, /^[0-9a-f]{40}$/);
  assert.equal(record.pullRequests[1].repo, 'master');
  assert.equal(record.pullRequests[1].url, 'https://github.com/example/master/pull/2');
  assert.equal(run(w.master, ['check', join(w.master, 'intent/demo', CHANGE, 'change.jsonc')]).status, 0);
  assert.deepEqual(git(w.master, 'log', '-2', '--format=%s').split('\n'), [`docs(demo): record pull requests ${CHANGE}`, `docs(demo): record pull requests ${CHANGE}`]);
  const shown = run(w.master, ['change', 'show', '--json', '--change', CHANGE, '--refresh'], w.gh.env);
  assert.deepEqual(Object.values(shown.json.states), ['OPEN', 'OPEN']);
});

test('done run again keeps the open pull requests and opens none', () => {
  const w = ready();
  assert.equal(done(w).json.outcome, 'passed');
  const again = done(w);
  assert.equal(again.json.outcome, 'passed', JSON.stringify(again.json));
  assert.equal(w.gh.calls().filter((c) => c.args[1] === 'create').length, 2);
  assert.equal(w.gh.prs().length, 2);
});

test('done refuses, pushing nothing, without a current review, with a task unlogged, or with uncommitted files', () => {
  const noReview = workspace({ built: true });
  git(noReview.master, 'checkout', '-q', '-b', `change/${CHANGE}`);
  commit(noReview.app, { 'a.txt': 'a' }, 'feat(demo): a');
  let r = run(noReview.master, ['run', 'change.done', '--json', '--change', CHANGE], fakeGitHub().env);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems.join('\n'), /has no review yet/);

  const stale = ready();
  commit(stale.app, { 'later.txt': 'x' }, 'fix(demo): later');
  r = done(stale);
  assert.match(r.json.problems.join('\n'), /the latest review covered app at [0-9a-f]{7}, but its branch is now at/);
  assert.equal(remoteHas(stale.app, `change/${CHANGE}`), false);

  const dirty = ready();
  write(join(dirty.app, 'stray.txt'), 'x');
  r = done(dirty);
  assert.match(r.json.problems.join('\n'), /app has uncommitted files: stray\.txt/);
  assert.equal(dirty.gh.calls().length, 0, 'gh was called before the change was verified');
});

const finding = (id, severity, text) =>
  `\n### ${id}\n\n\`\`\`jsonc\n${JSON.stringify({ id, severity, repo: 'app', concern: 'bug', file: 'hello.txt', line: 1 }, null, 2)}\n\`\`\`\n\n${text}\n`;

test('done refuses, pushing nothing, while the latest review has high findings, and lists each', () => {
  const w = ready();
  const file = `intent/demo/${CHANGE}/build/review.md`;
  commit(w.master, { [file]: review(w.head) + finding('F1', 'high', 'The greeting is lost on retry.') + finding('F2', 'medium', 'A name.') + finding('F3', 'high', 'Says hi twice.') }, `docs(demo): record review 1 of ${CHANGE}`);
  const r = done(w);
  assert.equal(r.status, 1, r.stdout);
  assert.equal(r.json.outcome, 'refused');
  const all = r.json.problems.join('\n');
  assert.match(all, /the latest review \(review 1\) has unresolved high findings: F1 \(app, hello\.txt:1\): The greeting is lost on retry\.; F3 \(app, hello\.txt:1\): Says hi twice\./);
  assert.doesNotMatch(all, /F2/);
  assert.equal(remoteHas(w.app, `change/${CHANGE}`), false);
  assert.deepEqual(w.gh.prs(), []);
  const second = `\n## Review 2\n\n\`\`\`jsonc\n${JSON.stringify({ review: 2, reviewed_at: '2026-11-02', commits: { app: w.head.slice(0, 7) } }, null, 2)}\n\`\`\`\n` + finding('F4', 'low', 'Fine.');
  commit(w.master, { [file]: readFileSync(join(w.master, file), 'utf8') + second }, `docs(demo): record review 2 of ${CHANGE}`);
  const again = done(w);
  assert.equal(again.json.outcome, 'passed', JSON.stringify(again.json));
});

test('a check that fails at the head being declared done stops it before anything is pushed', () => {
  const w = ready({ marker: { hooks: { 'build.finish': [nodeHook('unit', 'console.log("2 tests failed"); process.exitCode = 1')] } } });
  const r = done(w);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems.join('\n'), /app's checks \(build\.finish\) did not pass .*\n?.*2 tests failed/);
  assert.equal(remoteHas(w.app, `change/${CHANGE}`), false);
});

test('done refuses when the engineer is not signed in to GitHub, or the branch is taken on a remote', () => {
  const signedOut = ready();
  let r = done(signedOut, { FAKE_GH_AUTH: 'fail' });
  assert.match(r.json.problems.join('\n'), /not signed in to GitHub with gh: run gh auth login/);
  assert.equal(remoteHas(signedOut.app, `change/${CHANGE}`), false);

  const taken = ready();
  const other = join(taken.ws, 'other');
  git(taken.ws, 'clone', '-q', `${taken.app}.remote.git`, other);
  git(other, 'checkout', '-q', '-b', `change/${CHANGE}`);
  commit(other, { 'theirs.txt': 'x' }, 'feat(x): theirs');
  git(other, 'push', '-q', 'origin', `change/${CHANGE}`);
  r = done(taken);
  assert.match(r.json.problems.join('\n'), /already taken on app's remote by work that is not on this machine's branch/);
});

test('a pre-push hook that refuses stops done and says what was pushed', () => {
  const w = ready();
  write(join(w.master, '.git', 'hooks', 'pre-push'), '#!/bin/sh\necho "no pushing today" >&2\nexit 1\n');
  chmodSync(join(w.master, '.git', 'hooks', 'pre-push'), 0o755);
  const r = done(w);
  assert.equal(r.json.outcome, 'failed');
  assert.match(r.json.problems[0], /pushing change\/2026-11-01-greeting in master failed .*no pushing today/s);
  assert.match(r.json.problems[1], /pushed so far: app; running done again carries on from there/);
  assert.equal(w.gh.prs().length, 1);
});

test("the pull request title follows the team's pattern on the base branch", () => {
  const w = ready({ marker: { systems: { demo: { repos: ['app'], naming: { branch: 'change/{change}', pullRequestTitle: '{type}({scope}): {title}' } } } } });
  assert.equal(done(w).json.outcome, 'passed');
  assert.deepEqual(w.gh.prs().map((p) => p.title), ['feat(demo): Greeting', 'feat(demo): Greeting']);
});

test('a failure opening a pull request stops done after its push', () => {
  const w = ready();
  const r = done(w, { FAKE_GH_CREATE: 'fail' });
  assert.equal(r.json.outcome, 'failed');
  assert.match(r.json.problems[0], /opening the pull request in app failed: GraphQL: something went wrong/);
  assert.match(r.json.problems[1], /pushed so far: app/);
});

test('an HOSHIN_GH that is not a list of the program and its arguments stops done with a message', () => {
  for (const bad of ['gh', '[]', '[""]', '["gh", 3]']) {
    const w = ready();
    const r = done(w, { HOSHIN_GH: bad });
    assert.notEqual(r.json?.outcome, 'passed', bad);
    assert.match(r.stdout + r.stderr, /HOSHIN_GH must be a JSON list of the gh program and its arguments, as non-empty strings/, bad);
    assert.equal(remoteHas(w.app, `change/${CHANGE}`), false, bad);
  }
});

test('a change started with change start goes from its copy to pull requests', () => {
  const w = workspace({ marker: { hooks: { 'build.finish': [nodeHook('unit', '0')] } } });
  const gh = fakeGitHub();
  const name = '2026-11-03-wave';
  const started = run(w.master, ['change', 'start', '--json', '--system', 'demo', '--change', name, '--type', 'feat', '--scope', 'PROJ-9', '--title', 'Wave hello']);
  const copy = started.json.details.copy;
  const folder = `intent/demo/${name}`;
  const plan = readFileSync(join(testsRoot, 'Examples', 'change', '2', 'valid', 'plan.md'), 'utf8');
  const spec = readFileSync(join(testsRoot, 'Examples', 'change', '2', 'valid', 'spec.md'), 'utf8');
  commit(copy, { [`${folder}/intent.md`]: INTENT.replace('"Greeting"', '"Wave hello"').replace('"scope": "demo"', '"scope": "PROJ-9"'), [`${folder}/spec.md`]: spec, [`${folder}/plan.md`]: plan }, `docs(PROJ-9): write plan ${name}`);
  assert.equal(run(w.app, ['run', 'build.start', '--json', '--change', name]).json.outcome, 'passed');
  const head = commit(w.app, { 'wave.txt': 'o/' }, 'feat(PROJ-9): wave');
  commit(copy, { [`${folder}/build/app.md`]: buildLog(['T1', 'T2', 'T3']) }, `docs(PROJ-9): log T3 of ${name}`);
  assert.equal(run(w.app, ['run', 'build.finish', '--json', '--change', name]).json.outcome, 'passed');
  commit(copy, { [`${folder}/build/review.md`]: review(head) }, `docs(PROJ-9): record review 1 of ${name}`);
  const r = run(copy, ['run', 'change.done', '--json', '--change', name], gh.env);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  assert.deepEqual(gh.prs().map((p) => [p.repo, p.head, p.title]), [['app', `change/${name}`, 'Wave hello'], [name, `change/${name}`, 'Wave hello']]);
  assert.equal(git(copy, 'ls-remote', '--heads', 'origin', `change/${name}`).split(/\s/)[0], git(copy, 'rev-parse', 'HEAD'));
  assert.equal(git(w.master, 'branch', '--show-current'), 'main', "the engineer's checkout moved");
});
