import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { CHANGE, buildLog, commit, git, run, workspace, write } from './helpers/workspace.mjs';
import { intent, plan, review, spec } from './helpers/documents.mjs';
import { systemWorkspace } from './helpers/system-fixture.mjs';

const FOLDER = `intent/demo/${CHANGE}`;

const M = {
  uncommitted: (file) => `\`${file}\` has uncommitted changes. Commit the acceptance first, so history shows what was accepted.`,
  light: `\`${CHANGE}\` is on the light track; \`fix --accept ${CHANGE}\` finishes or revises its documents.`,
  intentNot: (s) => `This intent is \`${s}\`. The product owner accepts it by changing its status to \`accepted\` and committing; the spec runs only after that.`,
  specNot: (s) => `This spec is \`${s}\`. The product owner accepts it by changing its status to \`accepted\` and committing.`,
  intentReopened: (s) => `The intent was reopened (\`${s}\`). The spec must be regenerated and accepted again before planning.`,
  specPinMissing: 'This spec does not record the intent it was written from. Rerun the spec skill; the product owner accepts it again before planning.',
  specPinBroken: 'This spec was written from an earlier version of the intent. Rerun the spec skill; the product owner accepts it again before planning.',
  noScenarios: 'This spec has no Gherkin scenarios. Rerun the spec skill to regenerate it; the product owner accepts it again before planning.',
  planNot: (s) => `This plan is \`${s}\`. The engineer accepts it by changing its status to \`accepted\` and committing.`,
  reopened: (file, s) => `\`${file}\` was reopened (\`${s}\`). Rerun the skills after it, then accept the plan again.`,
  buildSpecPin: 'The intent changed after this spec was written. Rerun the spec skill, then the plan skill.',
  buildPlanPin: 'This plan was not written from the spec that is accepted now. Rerun the plan skill.',
  planUnreadable: 'This plan cannot be read. Rerun the plan skill.',
  unlogged: (list) => `These tasks have no build log entry yet: ${list}.`,
  logDirty: (repo) => `The build log for \`${repo}\` has uncommitted changes; rerun the build for \`${repo}\` to commit them.`,
};

function state(cwd, ...args) {
  const r = run(cwd, ['state', '--json', ...args]);
  assert.equal(r.status, 0, r.stderr);
  return r.json;
}

function changeOf(cwd, name = CHANGE, ...args) {
  const out = state(cwd, ...args);
  const found = out.changes.find((c) => c.change === name);
  assert.ok(found, `no change ${name} in ${JSON.stringify(out)}`);
  return found;
}

const head = (dir) => git(dir, 'rev-parse', 'HEAD');
const file = (dir, name) => join(dir, FOLDER, name);

function accepted({ scenarios = 2, tasks, repos, marker } = {}) {
  const w = workspace({ ...(repos ? { repos } : {}), ...(marker ? { marker } : {}) });
  const intentSha = head(w.master);
  const specSha = commit(w.master, { [`${FOLDER}/spec.md`]: spec('accepted', intentSha, scenarios) });
  commit(w.master, { [`${FOLDER}/plan.md`]: plan('accepted', specSha, tasks) });
  return { ...w, intentSha, specSha };
}

function built(opts) {
  const w = accepted(opts);
  commit(w.master, { [`${FOLDER}/build/app.md`]: buildLog(['T1', 'T2']) });
  return w;
}

function assertValidSchedule(s) {
  assert.ok(s && typeof s === 'object');
  assert.ok(Array.isArray(s.repos));
  assert.ok(['build', 'review', 'blocked'].includes(s.next));
  assert.equal(typeof s.oneAtATime, 'boolean');
  for (const r of s.repos) {
    assert.equal(typeof r.repo, 'string');
    assert.ok(['ready', 'waiting', 'done'].includes(r.state));
    assert.ok(r.task === null || typeof r.task === 'string');
    assert.ok(Array.isArray(r.waitingOn));
  }
}

test('the output names the system and every change, sorted, with the document facts', () => {
  const w = workspace();
  commit(w.master, { 'intent/demo/2026-10-01-earlier/intent.md': intent('draft') });
  const out = state(w.master);
  assert.equal(out.system, 'demo');
  assert.deepEqual(out.problems, []);
  assert.deepEqual(out.changes.map((c) => c.change), ['2026-10-01-earlier', CHANGE]);
  const c = out.changes[1];
  assert.equal(c.master, w.master);
  assert.equal(c.folder, join(w.master, FOLDER));
  assert.equal(c.inCopy, false);
  assert.equal(c.title, 'Greeting');
  assert.equal(c.track, 'full');
  assert.deepEqual(c.documents.intent, { exists: true, inHead: true, headStatus: 'accepted', workingStatus: 'accepted', uncommitted: false });
  assert.deepEqual(c.documents.spec, { exists: false, inHead: false, headStatus: null, workingStatus: null, uncommitted: false, pin: null });
  assert.deepEqual(c.documents.plan, { exists: false, inHead: false, headStatus: null, workingStatus: null, uncommitted: false, pin: null });
  assert.equal(c.scenarios, null);
  assert.equal(c.tasks, null);
  assert.deepEqual(c.buildLogs, {});
  assert.deepEqual(c.review, { committed: 0, draft: 'none' });
  assert.equal(c.fix, null);
  assert.deepEqual(Object.keys(c.ready), ['spec', 'plan', 'build', 'review', 'done', 'reject']);
});

test('an uncommitted intent is found from the working tree, and a deleted one from HEAD', () => {
  const w = workspace();
  write(join(w.master, 'intent/demo/2026-11-02-new/intent.md'), intent('draft'));
  rmSync(file(w.master, 'intent.md'));
  const out = state(w.master);
  const names = out.changes.map((c) => c.change);
  assert.deepEqual(names, [CHANGE, '2026-11-02-new']);
  const fresh = out.changes[1];
  assert.deepEqual(fresh.documents.intent, { exists: true, inHead: false, headStatus: null, workingStatus: 'draft', uncommitted: true });
  const gone = out.changes[0];
  assert.deepEqual(gone.documents.intent, { exists: false, inHead: true, headStatus: 'accepted', workingStatus: null, uncommitted: true });
  assert.equal(gone.title, 'Greeting');
});

test('--change limits the output to that change; an unknown change, system, or several systems without --system exit 2', () => {
  const w = workspace({ masterFiles: { 'intent/demo/2026-10-01-earlier/intent.md': intent('draft') } });
  assert.deepEqual(state(w.master, '--change', CHANGE).changes.map((c) => c.change), [CHANGE]);
  assert.equal(run(w.master, ['state', '--json', '--change', '2026-01-01-none']).status, 2);
  assert.equal(run(w.master, ['state', '--json', '--system', 'other']).status, 2);
  assert.equal(run(w.master, ['state']).status, 2);

  const two = workspace({ marker: { systems: { demo: { repos: ['app'] }, other: { repos: ['app'] } } } });
  const r = run(two.master, ['state', '--json']);
  assert.equal(r.status, 2);
  assert.match(r.stderr, /--system/);
  assert.equal(state(two.master, '--system', 'other').system, 'other');
  assert.deepEqual(state(two.master, '--system', 'other').changes, []);
  assert.equal(state(two.master, '--change', CHANGE).system, 'demo');
});

function copy(w, change) {
  const path = join(w.ws, '.hoshin', 'copies', change);
  git(w.master, 'worktree', 'add', '-q', '-b', `change/${change}`, path);
  return path;
}

test('a change found only in its copy is reported from the copy, from the main checkout', () => {
  const w = workspace();
  const other = '2026-11-03-copied';
  const path = copy(w, other);
  commit(path, { [`intent/demo/${other}/intent.md`]: intent('draft') });
  const c = changeOf(w.master, other);
  assert.equal(c.master, path);
  assert.equal(c.folder, join(path, 'intent/demo', other));
  assert.equal(c.inCopy, true);
  assert.equal(c.documents.intent.headStatus, 'draft');
  const mine = changeOf(w.master);
  assert.equal(mine.master, w.master);
  assert.equal(mine.inCopy, false);
});

test('a change in both the master and its own copy is reported from the copy', () => {
  const w = workspace();
  const path = copy(w, CHANGE);
  commit(path, { [`${FOLDER}/intent.md`]: intent('accepted', { title: 'Greeting in the copy' }) });
  const out = state(w.master);
  assert.equal(out.changes.length, 1);
  assert.equal(out.changes[0].master, path);
  assert.equal(out.changes[0].title, 'Greeting in the copy');
  assert.equal(changeOf(path).master, path);
});

test('pins: holds, broken, missing, pending acceptance, not a commit', () => {
  const w = accepted();
  let c = changeOf(w.master);
  assert.equal(c.documents.spec.pin, 'holds');
  assert.equal(c.documents.plan.pin, 'holds');
  assert.equal(c.scenarios, 2);

  commit(w.master, { [`${FOLDER}/intent.md`]: intent('accepted', { kind: 'feature' }) });
  c = changeOf(w.master);
  assert.equal(c.documents.spec.pin, 'broken');
  assert.equal(c.documents.plan.pin, 'holds');

  write(file(w.master, 'spec.md'), spec('accepted', 'pending acceptance'));
  assert.equal(changeOf(w.master).documents.spec.pin, 'missing');
  write(file(w.master, 'spec.md'), spec('accepted', '0123456789012345678901234567890123456789'));
  assert.equal(changeOf(w.master).documents.spec.pin, 'missing');
  write(file(w.master, 'spec.md'), spec('accepted', '').replace('  "source_intent_commit": "",\n', ''));
  assert.equal(changeOf(w.master).documents.spec.pin, 'missing');
});

test('uncommitted: an edited document is, an unchanged one is not', () => {
  const w = accepted();
  write(file(w.master, 'plan.md'), readFileSync(file(w.master, 'plan.md'), 'utf8') + '\nMore.\n');
  const c = changeOf(w.master);
  assert.equal(c.documents.plan.uncommitted, true);
  assert.equal(c.documents.spec.uncommitted, false);
});

test('scenarios: the count the spec reader finds, null when the spec does not read cleanly', () => {
  const w = accepted({ scenarios: 3 });
  assert.equal(changeOf(w.master).scenarios, 3);
  write(file(w.master, 'spec.md'), '```jsonc\n{ "layout": 2, "status": "draft" }\n```\n\n# Spec\n\nNo block.\n');
  assert.equal(changeOf(w.master).scenarios, null);
});

test('tasks come from the plan and build logs in HEAD; an uncommitted log entry does not count', () => {
  const w = accepted({ tasks: [['T1', 'app'], ['T2', 'app'], ['T3', 'web']], repos: ['app', 'web'] });
  commit(w.master, { [`${FOLDER}/build/app.md`]: buildLog(['T1']) });
  write(file(w.master, 'build/app.md'), buildLog(['T1', 'T2']));
  const c = changeOf(w.master);
  assert.deepEqual(c.tasks, {
    app: { all: ['T1', 'T2'], logged: ['T1'], unlogged: ['T2'], commits: { T1: [], T2: [] }, untasked: [], uncommitted: [] },
    web: { all: ['T3'], logged: [], unlogged: ['T3'], commits: { T3: [] }, untasked: [], uncommitted: [] },
  });
  assert.deepEqual(c.buildLogs, { app: 'added-entries' });
});

function schedule(tasks, logged = {}) {
  const repos = [...new Set(tasks.map((t) => t[1]))];
  const w = accepted({ tasks, repos });
  const logs = Object.fromEntries(Object.entries(logged).map(([repo, ids]) => [`${FOLDER}/build/${repo}.md`, buildLog(ids)]));
  if (Object.keys(logs).length > 0) commit(w.master, logs);
  const c = changeOf(w.master);
  assertValidSchedule(c.schedule);
  return c.schedule;
}

test('schedule: a repository is ready at its first unlogged task, done when it has none, in plan order', () => {
  assert.deepEqual(schedule([['T1', 'app'], ['T2', 'web'], ['T3', 'app'], ['T4', 'docs']], { app: ['T1'], docs: ['T4'] }), {
    repos: [
      { repo: 'app', state: 'ready', task: 'T3', waitingOn: [] },
      { repo: 'web', state: 'ready', task: 'T2', waitingOn: [] },
      { repo: 'docs', state: 'done', task: null, waitingOn: [] },
    ],
    next: 'build',
    oneAtATime: false,
  });
});

test('schedule: a repository waits on the unlogged tasks of other repositories its first unlogged task depends on', () => {
  const tasks = [['T1', 'api'], ['T2', 'api'], ['T3', 'web', ['T1', 'T2']], ['T4', 'web']];
  assert.deepEqual(schedule(tasks).repos, [
    { repo: 'api', state: 'ready', task: 'T1', waitingOn: [] },
    { repo: 'web', state: 'waiting', task: 'T3', waitingOn: [{ task: 'T1', repo: 'api' }, { task: 'T2', repo: 'api' }] },
  ]);
  assert.deepEqual(schedule(tasks, { api: ['T1'] }).repos[1], { repo: 'web', state: 'waiting', task: 'T3', waitingOn: [{ task: 'T2', repo: 'api' }] });
  assert.deepEqual(schedule(tasks, { api: ['T1', 'T2'] }).repos[1], { repo: 'web', state: 'ready', task: 'T3', waitingOn: [] });
});

test('schedule: a dependency in the same repository, on a logged task, or on a task the plan does not have does not make it wait, and one named twice is listed once', () => {
  const s = schedule([['T1', 'api'], ['T2', 'web', ['T1', 'T9']], ['T3', 'web', ['T2']], ['T4', 'docs', ['T3', 'T3']]], { api: ['T1'] });
  assert.deepEqual(s.repos, [
    { repo: 'api', state: 'done', task: null, waitingOn: [] },
    { repo: 'web', state: 'ready', task: 'T2', waitingOn: [] },
    { repo: 'docs', state: 'waiting', task: 'T4', waitingOn: [{ task: 'T3', repo: 'web' }] },
  ]);
  assert.equal(s.next, 'build');
});

test('schedule: only the first unlogged task of a repository decides, whatever a later one depends on', () => {
  const s = schedule([['T1', 'api'], ['T2', 'web'], ['T3', 'api', ['T2']]]);
  assert.deepEqual(s.repos[0], { repo: 'api', state: 'ready', task: 'T1', waitingOn: [] });
});

test('schedule: next is review once every task is logged, and blocked when tasks are left and no repository is ready', () => {
  assert.equal(schedule([['T1', 'api'], ['T2', 'web']], { api: ['T1'], web: ['T2'] }).next, 'review');
  const stuck = schedule([['T1', 'api', ['T2']], ['T2', 'web', ['T1']]]);
  assert.deepEqual(stuck.repos.map((r) => r.state), ['waiting', 'waiting']);
  assert.equal(stuck.next, 'blocked');
});

test('schedule: null when the plan cannot be read', () => {
  const w = accepted();
  commit(w.master, { [`${FOLDER}/plan.md`]: readFileSync(file(w.master, 'plan.md'), 'utf8').replace('"id": "T1"', '"id": "nope"') });
  const c = changeOf(w.master);
  assert.equal(c.tasks, null);
  assert.equal(c.schedule, null);
});

function work(dir, name, task) {
  write(join(dir, name), `${name}\n`);
  git(dir, 'add', '.');
  git(dir, 'commit', '-q', '-m', `feat(demo): ${name}`, '-m', task === undefined ? 'Generated-By: hoshin' : `Generated-By: hoshin\nPlan-Task: ${task}`);
  return head(dir);
}

test('tasks: commits by Plan-Task trailer from the merge base with the base branch, oldest first', () => {
  const w = accepted();
  work(w.app, 'before.txt', 'T1');
  git(w.app, 'checkout', '-q', '-b', `change/${CHANGE}`);
  const a = work(w.app, 'a.txt', 'T1');
  const b = work(w.app, 'b.txt');
  const c = work(w.app, 'c.txt', 'T2');
  const d = work(w.app, 'd.txt', 'T1');
  const t = changeOf(w.master).tasks.app;
  assert.deepEqual(t.commits, { T1: [a, d], T2: [c] });
  assert.deepEqual(t.untasked, [b]);
  assert.deepEqual(t.uncommitted, []);
});

test('tasks: a repository with no base branch has commits null', () => {
  const w = accepted();
  git(w.app, 'checkout', '-q', '-b', 'work');
  git(w.app, 'branch', '-q', '-D', 'main');
  git(w.app, 'remote', 'remove', 'origin');
  const t = changeOf(w.master).tasks.app;
  assert.equal(t.commits, null);
  assert.equal(t.untasked, null);
  assert.deepEqual(t.uncommitted, []);
});

test('tasks: uncommitted files of a product repository, every one', () => {
  const w = accepted();
  write(join(w.app, 'README.md'), 'changed\n');
  write(join(w.app, 'greet.ts'), 'x\n');
  write(join(w.app, 'intent', 'notes.md'), 'x\n');
  assert.deepEqual(changeOf(w.master).tasks.app.uncommitted.sort(), ['README.md', 'greet.ts', 'intent/'].sort());
});

test("tasks: in the master listed as a product repository, hoshin's own files are not uncommitted", () => {
  const w = accepted({ tasks: [['T1', 'app'], ['T2', 'master']], marker: { systems: { demo: { repos: ['app', 'master'] } } } });
  write(join(w.master, 'tool.sh'), 'echo\n');
  write(file(w.master, 'notes.md'), 'x\n');
  write(join(w.master, '.hoshin.json'), readFileSync(join(w.master, '.hoshin.json'), 'utf8') + '\n');
  const t = changeOf(w.master).tasks.master;
  assert.deepEqual(t.uncommitted, ['tool.sh']);
  assert.deepEqual(t.commits, { T2: [] });
  assert.deepEqual(t.untasked, []);
});

test("tasks: the master listed as a product repository is read in the change's copy when the change has one", () => {
  const w = accepted({ tasks: [['T1', 'app'], ['T2', 'master']], marker: { systems: { demo: { repos: ['app', 'master'] } } } });
  const copyPath = join(w.ws, '.hoshin', 'copies', CHANGE);
  git(w.master, 'worktree', 'add', '-q', '-b', `change/${CHANGE}`, copyPath);
  const sha = work(copyPath, 'tool.sh', 'T2');
  write(join(copyPath, 'more.sh'), 'echo\n');
  write(join(w.master, 'elsewhere.sh'), 'echo\n');
  const t = changeOf(w.master).tasks.master;
  assert.deepEqual(t.commits, { T2: [sha] });
  assert.deepEqual(t.untasked, []);
  assert.deepEqual(t.uncommitted, ['more.sh']);
});

test('tasks: a repository that is not checked out has commits and uncommitted null', () => {
  const w = accepted({ tasks: [['T1', 'app'], ['T2', 'web']], marker: { systems: { demo: { repos: ['app', 'web'] } } } });
  assert.deepEqual(changeOf(w.master).tasks.web, { all: ['T2'], logged: [], unlogged: ['T2'], commits: null, untasked: null, uncommitted: null });
});

test('buildLogs: clean, added-entries (tracked and untracked), other', () => {
  const w = built();
  assert.deepEqual(changeOf(w.master).buildLogs, { app: 'clean' });
  write(file(w.master, 'build/web.md'), buildLog(['T9']));
  assert.deepEqual(changeOf(w.master).buildLogs, { app: 'clean', web: 'added-entries' });
  write(file(w.master, 'build/app.md'), buildLog(['T1']));
  assert.deepEqual(changeOf(w.master).buildLogs, { app: 'other', web: 'added-entries' });
  write(file(w.master, 'build/review.md'), review(1));
  assert.deepEqual(Object.keys(changeOf(w.master).buildLogs), ['app', 'web']);
});

test('review: committed sections, and the draft none, next-section (tracked and untracked), other', () => {
  const w = built();
  write(file(w.master, 'build/review.md'), review(1));
  assert.deepEqual(changeOf(w.master).review, { committed: 0, draft: 'next-section' });
  write(file(w.master, 'build/review.md'), review(2));
  assert.deepEqual(changeOf(w.master).review, { committed: 0, draft: 'other' });
  commit(w.master, { [`${FOLDER}/build/review.md`]: review(1) });
  assert.deepEqual(changeOf(w.master).review, { committed: 1, draft: 'none' });
  write(file(w.master, 'build/review.md'), review(2));
  assert.deepEqual(changeOf(w.master).review, { committed: 1, draft: 'next-section' });
  write(file(w.master, 'build/review.md'), review(1).replace('abc1230', 'fff9999'));
  assert.deepEqual(changeOf(w.master).review, { committed: 1, draft: 'other' });
  write(file(w.master, 'build/review.md'), review(3));
  assert.deepEqual(changeOf(w.master).review, { committed: 1, draft: 'other' });
});

test('spec: stops on an intent not accepted, then on an uncommitted acceptance; candidate until the spec is accepted and pinned', () => {
  const w = workspace({ masterFiles: { [`${FOLDER}/intent.md`]: intent('draft') } });
  assert.deepEqual(changeOf(w.master).ready.spec, { candidate: false, stop: M.intentNot('draft'), accept: [] });
  write(file(w.master, 'intent.md'), intent('accepted'));
  assert.deepEqual(changeOf(w.master).ready.spec, { candidate: false, stop: M.uncommitted('intent.md'), accept: [] });
  commit(w.master, {});
  assert.deepEqual(changeOf(w.master).ready.spec, { candidate: true, stop: null, accept: ['intent.md'] });
  commit(w.master, { [`${FOLDER}/spec.md`]: spec('draft') });
  assert.equal(changeOf(w.master).ready.spec.candidate, true);
  commit(w.master, { [`${FOLDER}/spec.md`]: spec('accepted', head(w.master)) });
  assert.equal(changeOf(w.master).ready.spec.candidate, false);
  commit(w.master, { [`${FOLDER}/intent.md`]: intent('accepted', { kind: 'feature' }) });
  assert.equal(changeOf(w.master).ready.spec.candidate, true);
});

test('plan: every stop, in order', () => {
  const w = workspace();
  const p = () => changeOf(w.master).ready.plan;
  assert.equal(p().stop, M.specNot('missing'));
  commit(w.master, { [`${FOLDER}/spec.md`]: spec('draft') });
  assert.equal(p().stop, M.specNot('draft'));
  write(file(w.master, 'intent.md'), intent('draft'));
  write(file(w.master, 'spec.md'), spec('accepted'));
  assert.equal(p().stop, M.intentReopened('draft'));
  write(file(w.master, 'intent.md'), intent('accepted'));
  assert.equal(p().stop, M.uncommitted('spec.md'));
  commit(w.master, {});
  assert.equal(p().stop, M.specPinMissing);
  const intentSha = head(w.master);
  commit(w.master, { [`${FOLDER}/spec.md`]: spec('accepted', intentSha) });
  commit(w.master, { [`${FOLDER}/intent.md`]: intent('accepted', { kind: 'feature' }) });
  assert.equal(p().stop, M.specPinBroken);
  commit(w.master, { [`${FOLDER}/spec.md`]: spec('accepted', head(w.master), 0) });
  assert.equal(p().stop, M.noScenarios);
  commit(w.master, { [`${FOLDER}/spec.md`]: spec('accepted', git(w.master, 'log', '-1', '--format=%H', '--', `${FOLDER}/intent.md`), 2) });
  assert.deepEqual(p(), { candidate: true, stop: null, accept: ['intent.md', 'spec.md'] });
});

test('plan: a candidate with no plan or a superseded one, not with a draft or accepted one', () => {
  const w = accepted();
  assert.equal(changeOf(w.master).ready.plan.candidate, false);
  commit(w.master, { [`${FOLDER}/plan.md`]: plan('superseded', w.specSha) });
  assert.equal(changeOf(w.master).ready.plan.candidate, true);
  commit(w.master, { [`${FOLDER}/plan.md`]: plan('draft') });
  assert.equal(changeOf(w.master).ready.plan.candidate, false);
});

test('build: every stop, in order', () => {
  const w = accepted();
  const b = () => changeOf(w.master).ready.build;
  assert.deepEqual(b(), { candidate: true, stop: null, accept: ['intent.md', 'spec.md', 'plan.md'] });

  write(file(w.master, 'plan.md'), plan('draft', w.specSha));
  assert.equal(b().stop, M.planNot('draft'));
  git(w.master, 'checkout', '--', `${FOLDER}/plan.md`);

  write(file(w.master, 'spec.md'), spec('draft', w.intentSha));
  write(file(w.master, 'intent.md'), intent('draft'));
  assert.equal(b().stop, M.reopened('intent.md', 'draft'));
  git(w.master, 'checkout', '--', `${FOLDER}/intent.md`);
  assert.equal(b().stop, M.reopened('spec.md', 'draft'));
  git(w.master, 'checkout', '--', `${FOLDER}/spec.md`);

  write(file(w.master, 'plan.md'), plan('accepted', w.specSha) + '\nMore.\n');
  assert.equal(b().stop, M.uncommitted('plan.md'));
  git(w.master, 'checkout', '--', `${FOLDER}/plan.md`);

  commit(w.master, { [`${FOLDER}/intent.md`]: intent('accepted', { kind: 'feature' }) });
  assert.equal(b().stop, M.buildSpecPin);
  const specSha = commit(w.master, { [`${FOLDER}/spec.md`]: spec('accepted', head(w.master)) });
  assert.equal(b().stop, M.buildPlanPin);
  commit(w.master, { [`${FOLDER}/plan.md`]: plan('accepted', specSha).replace('## Tasks', '## Tasks\n\n### T1 - broken\n') });
  assert.equal(b().stop, M.planUnreadable);
});

test('build: no longer a candidate once every task is logged', () => {
  const w = built();
  assert.deepEqual(changeOf(w.master).ready.build, { candidate: false, stop: null, accept: ['intent.md', 'spec.md', 'plan.md'] });
});

test('review: build gate, then unlogged tasks, then an uncommitted build log', () => {
  const w = accepted({ tasks: [['T1', 'app'], ['T2', 'app'], ['T3', 'web']], repos: ['app', 'web'] });
  const r = () => changeOf(w.master).ready.review;
  write(file(w.master, 'plan.md'), plan('draft'));
  assert.equal(r().stop, M.planNot('draft'));
  git(w.master, 'checkout', '--', `${FOLDER}/plan.md`);
  assert.equal(r().stop, M.unlogged('`app`: T1, T2; `web`: T3'));
  commit(w.master, { [`${FOLDER}/build/app.md`]: buildLog(['T1', 'T2']), [`${FOLDER}/build/web.md`]: buildLog(['T3']) });
  assert.deepEqual(r(), { candidate: true, stop: null, accept: ['intent.md', 'spec.md', 'plan.md'] });
  write(file(w.master, 'build/web.md'), buildLog(['T3', 'T4']));
  assert.deepEqual(r(), { candidate: false, stop: M.logDirty('web'), accept: ['intent.md', 'spec.md', 'plan.md'] });
});

test('done: a candidate once a review is committed and every task is logged', () => {
  const w = built();
  assert.deepEqual(changeOf(w.master).ready.done, { candidate: false, stop: null, accept: [] });
  commit(w.master, { [`${FOLDER}/build/review.md`]: review(1) });
  assert.deepEqual(changeOf(w.master).ready.done, { candidate: true, stop: null, accept: [] });
});

test('reject: open for a draft or accepted change with no build work, never a candidate', () => {
  const draft = workspace({ masterFiles: { [`${FOLDER}/intent.md`]: intent('draft') } });
  assert.deepEqual(changeOf(draft.master).ready.reject, { candidate: false, stop: null, accept: [] });
  const w = accepted();
  assert.deepEqual(changeOf(w.master).ready.reject, { candidate: false, stop: null, accept: [] });
});

test('reject: stops on a rejected intent, then on build work, then on an uncommitted document', () => {
  const closed = `\`${CHANGE}\` is closed: its intent was rejected.`;
  const builtStop = `\`${CHANGE}\` has build work (a build log or a review), so it is not rejected. The engineer settles that work first.`;
  const gone = workspace({ masterFiles: { [`${FOLDER}/intent.md`]: intent('rejected') } });
  assert.equal(changeOf(gone.master).ready.reject.stop, closed);
  const w = accepted();
  write(file(w.master, 'build/app.md'), buildLog(['T1']));
  assert.equal(changeOf(w.master).ready.reject.stop, builtStop);
  commit(w.master, { [`${FOLDER}/build/app.md`]: buildLog(['T1']) });
  assert.equal(changeOf(w.master).ready.reject.stop, builtStop);
  const reviewed = accepted();
  commit(reviewed.master, { [`${FOLDER}/build/review.md`]: review(1) });
  assert.equal(changeOf(reviewed.master).ready.reject.stop, builtStop);
  const edited = accepted();
  write(file(edited.master, 'spec.md'), spec('draft'));
  assert.equal(changeOf(edited.master).ready.reject.stop, '`spec.md` has uncommitted changes. Commit or discard them first, so the rejection does not carry them unread.');
});

test('a fix-track change carries its fix-state result and is never a spec or plan candidate', () => {
  const w = workspace({ masterFiles: { [`${FOLDER}/intent.md`]: intent('accepted', { track: 'fix' }) } });
  const c = changeOf(w.master);
  assert.equal(c.track, 'fix');
  const fix = run(w.master, ['fix-state', '--json', join(w.master, FOLDER)]);
  assert.deepEqual(c.fix, fix.json);
  assert.deepEqual(c.ready.spec, { candidate: false, stop: M.light, accept: ['intent.md'] });
  assert.deepEqual(c.ready.plan, { candidate: false, stop: M.light, accept: ['intent.md'] });
  assert.equal(c.ready.build.stop, M.planNot('missing'));
});

test('a fix-track change whose folder is gone from the working tree has no fix object and a problem, as fix-state exits 2', () => {
  const w = workspace({ masterFiles: { [`${FOLDER}/intent.md`]: intent('accepted', { track: 'fix' }) } });
  rmSync(join(w.master, FOLDER), { recursive: true });
  const out = state(w.master);
  const c = out.changes.find((x) => x.change === CHANGE);
  assert.equal(c.track, 'fix');
  assert.equal(c.fix, null);
  assert.deepEqual(out.problems, [`${CHANGE}: ${c.folder} does not exist`]);
  assert.equal(run(w.master, ['fix-state', '--json', c.folder]).status, 2);
});

test('state writes nothing and takes no lock', () => {
  const w = accepted();
  write(file(w.master, 'plan.md'), plan('accepted', w.specSha) + '\nMore.\n');
  const before = readFileSync(join(w.master, '.git', 'index'));
  state(w.master);
  assert.deepEqual(readFileSync(join(w.master, '.git', 'index')), before);
  writeFileSync(join(w.master, '.git', 'index.lock'), '');
  assert.equal(changeOf(w.master).documents.plan.uncommitted, true);
});

test('pins: a pin whose upstream changed only its mode holds', () => {
  const w = accepted();
  git(w.master, 'update-index', '--chmod=+x', `${FOLDER}/intent.md`);
  git(w.master, 'commit', '-q', '-m', 'chore: mode');
  assert.equal(changeOf(w.master).documents.spec.pin, 'holds');
});

test('pins: not-a-commit values stay missing', () => {
  const w = accepted();
  const pinWith = (v) => {
    write(file(w.master, 'spec.md'), spec('accepted', v));
    return changeOf(w.master).documents.spec.pin;
  };
  assert.equal(pinWith(w.intentSha.slice(0, 7)), 'holds');
  assert.equal(pinWith('deadbee'), 'missing');
  assert.equal(pinWith(git(w.master, 'rev-parse', `HEAD:${FOLDER}/intent.md`)), 'missing');
  assert.equal(pinWith(git(w.master, 'rev-parse', 'HEAD^{tree}')), 'missing');
});

test('autocrlf: a line-ending-only difference is not uncommitted', () => {
  const w = accepted();
  git(w.master, 'config', 'core.autocrlf', 'true');
  const path = file(w.master, 'intent.md');
  write(path, readFileSync(path, 'utf8').replace(/\r?\n/g, '\r\n'));
  const c = changeOf(w.master);
  assert.equal(c.documents.intent.uncommitted, false);
});

test('--open reports only open changes, each exactly as the whole read does', () => {
  const f = systemWorkspace({ finished: 5 });
  const whole = state(f.master), open = state(f.master, '--open');
  assert.deepEqual(open.changes, whole.changes.filter((c) => c.finished === null));
  assert.ok(open.changes.length > 0 && open.changes.length < whole.changes.length);
});

test('--open with --change is a usage error', () => {
  const w = workspace();
  assert.equal(run(w.master, ['state', '--json', '--open', '--change', CHANGE]).status, 2);
});
