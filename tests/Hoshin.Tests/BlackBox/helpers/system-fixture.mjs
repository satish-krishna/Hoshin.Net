// A benchmark workspace for `state` with deterministic commits.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, realpathSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { FIX_BUILD_LOG, fixIntent, fixPlan, fixSpec, intent, plan, review, spec } from './documents.mjs';
import { buildLog, env, write } from './workspace.mjs';
import { tempDir } from './temp.mjs';

const DATE = '2026-01-01T00:00:00Z';
const datedEnv = { ...env, GIT_AUTHOR_DATE: DATE, GIT_COMMITTER_DATE: DATE };

function git(cwd, ...args) {
  const r = spawnSync('git', args, { cwd, encoding: 'utf8', env: datedEnv });
  assert.equal(r.status, 0, `git ${args.join(' ')} in ${cwd}: ${r.stderr}`);
  return r.stdout.trim();
}

function commit(dir, files, ...messages) {
  for (const [name, text] of Object.entries(files)) write(join(dir, name), text);
  git(dir, 'add', '.');
  git(dir, 'commit', '-q', ...messages.flatMap((m) => ['-m', m]));
  return git(dir, 'rev-parse', 'HEAD');
}

const docCommit = (dir, files) => commit(dir, files, 'docs(demo): change', 'Generated-By: hoshin');

function initRepo(path, files) {
  mkdirSync(path, { recursive: true });
  git(path, 'init', '-q', '-b', 'main');
  commit(path, files, 'chore: initial');
  const remote = `${path}.remote.git`;
  git(dirname(path), 'init', '-q', '--bare', '-b', 'main', remote);
  git(path, 'remote', 'add', 'origin', remote);
  git(path, 'push', '-q', '-u', 'origin', 'main');
  git(path, 'remote', 'set-head', 'origin', 'main');
}

const NAMES = {
  fixFinished: '2026-09-01-fix-finished',
  rejectedOnMain: '2026-09-02-rejected-on-main',
  mergedCopyKept: '2026-09-03-merged-copy-kept',
  draft: '2026-10-01-draft',
  specDraft: '2026-10-02-spec-draft',
  planned: '2026-10-03-planned',
  built: '2026-10-04-built',
  fixBuilt: '2026-10-05-fix-built',
  fixDirty: '2026-10-06-fix-dirty',
  fullDirty: '2026-10-07-full-dirty',
  rejectedOnWork: '2026-10-08-rejected-on-work',
  inCopy: '2026-10-09-in-copy',
};

const folder = (change) => `intent/demo/${change}`;

export function systemWorkspace({ finished, cleanOpen = 0 }) {
  const ws = realpathSync.native(tempDir('hoshin-sys-'));
  const master = join(ws, 'master');
  const app = join(ws, 'app');
  const names = { ...NAMES };
  const done = Array.from({ length: finished }, (_, i) => `2026-08-01-finished-${String(i).padStart(3, '0')}`);
  const open = Array.from({ length: cleanOpen }, (_, i) => `2026-10-20-clean-open-${String(i).padStart(3, '0')}`);
  const at = (change, file) => `${folder(change)}/${file}`;
  const files = (entries) => Object.fromEntries(entries.map(([change, file, text]) => [at(change, file), text]));

  initRepo(master, { '.hoshin.json': JSON.stringify({ version: 1, systems: { demo: { repos: ['app'] } } }, null, 2) + '\n' });
  initRepo(app, { 'README.md': '# app\n' });

  const m1 = docCommit(master, files([
    ...done.map((c) => [c, 'intent.md', intent('accepted')]),
    [names.fixFinished, 'intent.md', fixIntent('accepted', names.fixFinished)],
    [names.rejectedOnMain, 'intent.md', intent('rejected')],
    [names.mergedCopyKept, 'intent.md', intent('accepted')],
  ]));
  const m2 = docCommit(master, files([
    ...done.map((c) => [c, 'spec.md', spec('accepted', m1, 2, c)]),
    [names.fixFinished, 'spec.md', fixSpec('accepted', m1, names.fixFinished)],
  ]));
  docCommit(master, files([
    ...done.map((c) => [c, 'plan.md', plan('accepted', m2, undefined, c)]),
    [names.fixFinished, 'plan.md', fixPlan('accepted', m2, names.fixFinished)],
  ]));
  docCommit(master, files([
    ...done.flatMap((c) => [[c, 'build/app.md', buildLog(['T1', 'T2'])], [c, 'build/review.md', review(1)]]),
    [names.fixFinished, 'build/app.md', FIX_BUILD_LOG],
  ]));
  git(master, 'push', '-q', 'origin', 'main');

  git(master, 'checkout', '-q', '-b', 'work');
  const full = [names.specDraft, names.planned, names.built, names.fullDirty];
  const fix = [names.fixBuilt, names.fixDirty];
  const w1 = docCommit(master, files([
    [names.draft, 'intent.md', intent('draft')],
    ...full.map((c) => [c, 'intent.md', intent('accepted')]),
    ...open.map((c) => [c, 'intent.md', intent('accepted')]),
    ...fix.map((c) => [c, 'intent.md', fixIntent('accepted', c)]),
    [names.rejectedOnWork, 'intent.md', intent('rejected')],
  ]));
  const w2 = docCommit(master, files([
    [names.specDraft, 'spec.md', spec('draft', 'pending acceptance', 2, names.specDraft)],
    ...[names.planned, names.built, names.fullDirty, ...open].map((c) => [c, 'spec.md', spec('accepted', w1, 2, c)]),
    ...fix.map((c) => [c, 'spec.md', fixSpec('accepted', w1, c)]),
  ]));
  docCommit(master, files([
    ...[names.planned, names.built, names.fullDirty, ...open].map((c) => [c, 'plan.md', plan('accepted', w2, undefined, c)]),
    ...fix.map((c) => [c, 'plan.md', fixPlan('accepted', w2, c)]),
  ]));
  docCommit(master, files([
    [names.built, 'build/app.md', buildLog(['T1', 'T2'])],
    [names.built, 'build/review.md', review(1)],
    [names.fixBuilt, 'build/app.md', FIX_BUILD_LOG],
    ...open.flatMap((c) => [[c, 'build/app.md', buildLog(['T1', 'T2'])], [c, 'build/review.md', review(1)]]),
  ]));

  git(app, 'checkout', '-q', '-b', `change/${names.built}`);
  commit(app, { 'src/T1.ts': 'export const t1 = 1;\n' }, 'feat(app): do T1', 'Plan-Task: T1');
  commit(app, { 'src/T2.ts': 'export const t2 = 2;\n' }, 'feat(app): do T2', 'Plan-Task: T2');

  const copies = {};
  for (const key of ['inCopy', 'mergedCopyKept']) {
    copies[key] = join(ws, '.hoshin', 'copies', names[key]);
    git(master, 'worktree', 'add', '-q', '-b', `change/${names[key]}`, copies[key]);
  }
  docCommit(copies.inCopy, files([[names.inCopy, 'intent.md', intent('accepted')]]));

  for (const [change, text] of [[names.fixDirty, fixSpec('accepted', w1, names.fixDirty)], [names.fullDirty, spec('accepted', w1, 2, names.fullDirty)]]) {
    write(join(master, at(change, 'spec.md')), text + '\nOne more line.\n');
  }

  return { ws, master, app, copies, names };
}
