// build.start and build.finish per app in the single-repo layout.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildLog, commit, git, nodeHook, run } from './helpers/workspace.mjs';
import { singleRepo } from './helpers/single-repo.mjs';

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

const brokenCheck = (id = 'boot', rest = {}) =>
  nodeHook(id, `const fs=require('fs'); if (fs.existsSync('broken.txt')) { fs.writeFileSync(process.env.HOSHIN_FAILURES, fs.readFileSync('broken.txt','utf8')); process.exit(1) }`, rest);

function started({ hooks = {}, onMain = {} } = {}) {
  const s = singleRepo({ marker: { hooks } });
  if (Object.keys(onMain).length > 0) {
    commit(s.repo, onMain, 'chore: on main');
    git(s.repo, 'push', '-q', 'origin', 'main');
  }
  const r = run(s.repo, ['change', 'start', '--json', '--system', 'demo', '--change', NAME, '--type', 'feat', '--scope', 'PROJ-1', '--title', 'Wave']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  const copy = r.json.details.copy;
  commit(copy, { [`${DIR}/plan.md`]: PLAN, [`${DIR}/build/api.md`]: buildLog(['T1']), [`${DIR}/build/web.md`]: buildLog(['T2']) }, 'docs(PROJ-1): plan');
  return { ...s, copy };
}

const buildStart = (s, app) => run(s.repo, ['run', 'build.start', '--json', '--change', NAME, '--repo', app]);
const buildFinish = (s, app) => run(s.repo, ['run', 'build.finish', '--json', '--change', NAME, '--repo', app]);
const storeFile = (s, app) => join(git(s.repo, 'rev-parse', '--path-format=absolute', '--git-common-dir'), 'hoshin', 'demo', '.baselines', `${app}.json`);
const text = (r) => r.stdout + r.stderr;

test('build.start for an app passes without switching anything in the engineer checkout', () => {
  const s = started();
  const mainHead = git(s.repo, 'rev-parse', 'HEAD');
  const r = buildStart(s, 'api');
  assert.equal(r.json.outcome, 'passed', text(r));
  assert.equal(git(s.repo, 'branch', '--show-current'), 'main');
  assert.equal(git(s.repo, 'rev-parse', 'HEAD'), mainHead);
  assert.equal(git(s.copy, 'branch', '--show-current'), `change/${NAME}`);
  const gitDir = git(s.copy, 'rev-parse', '--path-format=absolute', '--git-dir');
  const active = JSON.parse(readFileSync(join(gitDir, 'hoshin', 'active-build.json'), 'utf8'));
  assert.equal(active.repo, 'api');
  assert.equal(active.change, NAME);
  assert.equal(existsSync(join(s.repo, '.git', 'hoshin', 'active-build.json')), false, 'the main checkout was written to');
});

test('a baseline takes the base in the copy as a whole and puts the copy back on the change branch, clean', () => {
  const s = started({ hooks: { 'build.finish': [brokenCheck()] } });
  const head = git(s.copy, 'rev-parse', 'HEAD');
  assert.notEqual(head, git(s.copy, 'rev-parse', 'main'), 'the copy is beyond the base, so the baseline detaches');
  const r = buildStart(s, 'api');
  assert.equal(r.json.outcome, 'passed', text(r));
  assert.equal(r.json.details.baseline.base, git(s.copy, 'rev-parse', 'main'));
  assert.equal(git(s.copy, 'branch', '--show-current'), `change/${NAME}`);
  assert.equal(git(s.copy, 'rev-parse', 'HEAD'), head);
  assert.equal(git(s.copy, 'status', '--porcelain'), '');
  assert.equal(git(s.repo, 'branch', '--show-current'), 'main');
  assert.ok(existsSync(storeFile(s, 'api')));
});

test("another app's uncommitted files do not block this app and survive its baseline, its own are reported", () => {
  const s = started({ hooks: { 'build.finish': [brokenCheck()] } });
  writeFileSync(join(s.copy, 'apps', 'web', 'wip.txt'), 'x');
  const ok = buildStart(s, 'api');
  assert.equal(ok.json.outcome, 'passed', text(ok));
  assert.deepEqual(ok.json.details.uncommitted, []);
  assert.equal(existsSync(join(s.copy, 'apps', 'web', 'wip.txt')), true, "the baseline removed another app's file");
  writeFileSync(join(s.copy, 'apps', 'api', 'wip.txt'), 'x');
  const own = buildStart(s, 'api');
  assert.deepEqual(own.json.details.uncommitted, ['apps/api/wip.txt']);
});

test('build.start for a second app is refused while the first is unfinished, and passes once its build.finish passed', () => {
  const s = started();
  assert.equal(buildStart(s, 'api').json.outcome, 'passed');
  const refused = buildStart(s, 'web');
  assert.equal(refused.json.outcome, 'refused', text(refused));
  assert.ok(refused.json.problems.includes('`api` is being built in this copy; build one app at a time'), JSON.stringify(refused.json.problems));
  assert.equal(buildStart(s, 'api').json.outcome, 'passed', 'the app being built may start again');
  const done = buildFinish(s, 'api');
  assert.equal(done.json.outcome, 'passed', text(done));
  const web = buildStart(s, 'web');
  assert.equal(web.json.outcome, 'passed', text(web));
  assert.equal(JSON.parse(readFileSync(join(git(s.copy, 'rev-parse', '--path-format=absolute', '--git-dir'), 'hoshin', 'active-build.json'), 'utf8')).repo, 'web');
});

test('a work commit in the other app after its build.finish makes that build unfinished again', () => {
  const s = started();
  buildStart(s, 'api');
  assert.equal(buildFinish(s, 'api').json.outcome, 'passed');
  commit(s.copy, { 'apps/api/more.txt': 'x' }, 'feat(PROJ-1): more api');
  const refused = buildStart(s, 'web');
  assert.equal(refused.json.outcome, 'refused', text(refused));
  assert.match(text(refused), /`api` is being built in this copy; build one app at a time/);
});

test("a check failing on the base for web does not affect api's judgment, and baselines are stored per app", () => {
  const s = started({ hooks: { 'build.finish': [brokenCheck()] }, onMain: { 'apps/web/broken.txt': 'w1\n' } });
  const api = buildStart(s, 'api');
  assert.equal(api.json.outcome, 'passed', text(api));
  assert.deepEqual(api.json.details.baseline.failing, []);
  assert.equal(buildFinish(s, 'api').json.outcome, 'passed');
  commit(s.copy, { 'apps/web/w.txt': 'w' }, 'feat(PROJ-1): web');
  const web = buildStart(s, 'web');
  assert.equal(web.json.outcome, 'passed', text(web));
  assert.deepEqual(web.json.details.baseline.failing, [{ id: 'boot', ids: ['w1'] }]);
  const stored = (app) => JSON.parse(readFileSync(storeFile(s, app), 'utf8'));
  assert.equal(stored('web').repo, 'web');
  assert.equal(stored('web').entries[0].hooks[0].outcome, 'failed');
  assert.equal(stored('api').repo, 'api');
  assert.equal(stored('api').entries[0].hooks[0].outcome, 'passed');
  const finished = buildFinish(s, 'web');
  assert.equal(finished.json.outcome, 'passed', text(finished));
  assert.deepEqual(finished.json.details.baseline.stillFailing, [{ id: 'boot', ids: ['w1'] }]);
  assert.equal(git(s.copy, 'branch', '--show-current'), `change/${NAME}`);
});

test('a build.finish hook with apps is selected for that app only, for its checks and its baseline', () => {
  const s = started({ hooks: { 'build.finish': [brokenCheck('web-check', { apps: ['web'] })] } });
  const api = buildStart(s, 'api');
  assert.equal(api.json.outcome, 'passed', text(api));
  assert.equal(existsSync(storeFile(s, 'api')), false, 'api has no checks, so no baseline');
  const apiDone = buildFinish(s, 'api');
  assert.equal(apiDone.json.outcome, 'passed', text(apiDone));
  assert.deepEqual(apiDone.json.hooks.map((h) => h.id), []);
  commit(s.copy, { 'apps/web/w.txt': 'w' }, 'feat(PROJ-1): web');
  const web = buildStart(s, 'web');
  assert.equal(web.json.outcome, 'passed', text(web));
  assert.ok(existsSync(storeFile(s, 'web')));
  const webDone = buildFinish(s, 'web');
  assert.equal(webDone.json.outcome, 'passed', text(webDone));
  assert.deepEqual(webDone.json.hooks.map((h) => h.id), ['web-check']);
});

test('the commit rules are checked once over the repository range, whichever app finishes', () => {
  const s = started();
  buildStart(s, 'api');
  writeFileSync(join(s.copy, 'apps', 'web', 'stray.txt'), 'x');
  git(s.copy, 'add', '.');
  git(s.copy, 'commit', '-q', '-m', 'wip: stray');
  const r = buildFinish(s, 'api');
  assert.equal(r.json.outcome, 'refused', text(r));
  assert.match(text(r), /break the commit rules/);
});

test('build.finish refuses a copy that is not on the change branch', () => {
  const s = started();
  buildStart(s, 'api');
  git(s.copy, 'checkout', '-q', '--detach');
  const r = buildFinish(s, 'api');
  assert.equal(r.json.outcome, 'refused', text(r));
  assert.match(text(r), /not on the change's branch/);
});

test('build.start refuses a copy that is not on the change branch, and switches nothing', () => {
  const s = started();
  git(s.copy, 'checkout', '-q', '--detach');
  const r = buildStart(s, 'api');
  assert.equal(r.json.outcome, 'refused', text(r));
  assert.equal(git(s.copy, 'branch', '--show-current'), '');
});

test("another app's changes to tracked files stop the baseline from checking out the base, and are left alone", () => {
  const s = started({ hooks: { 'build.finish': [brokenCheck()] } });
  writeFileSync(join(s.copy, 'apps', 'web', 'README.md'), 'edited\n');
  const r = buildStart(s, 'api');
  assert.equal(r.json.outcome, 'failed', text(r));
  assert.match(text(r), /uncommitted changes to tracked files.*apps\/web\/README\.md/);
  assert.equal(git(s.copy, 'branch', '--show-current'), `change/${NAME}`);
  assert.equal(readFileSync(join(s.copy, 'apps', 'web', 'README.md'), 'utf8'), 'edited\n');
  assert.equal(existsSync(storeFile(s, 'api')), false);
});

test('a baseline killed on the base commit is recovered before the change is looked up, by build.finish and build.start', () => {
  for (const event of ['build.finish', 'build.start']) {
    const s = started();
    const head = git(s.copy, 'rev-parse', 'HEAD');
    const gitDir = git(s.copy, 'rev-parse', '--path-format=absolute', '--git-dir');
    mkdirSync(join(gitDir, 'hoshin'), { recursive: true });
    writeFileSync(join(gitDir, 'hoshin', 'baseline-restore.json'), JSON.stringify({ branch: `change/${NAME}`, head, folder: 'apps/api' }) + '\n');
    git(s.copy, 'checkout', '-q', '--detach', 'main');
    assert.equal(existsSync(join(s.copy, DIR)), false, 'the base holds no change folder');
    const r = run(s.repo, ['run', event, '--json', '--change', NAME, '--repo', 'api']);
    assert.equal(r.json.outcome, 'passed', event + ' ' + text(r));
    assert.equal(git(s.copy, 'branch', '--show-current'), `change/${NAME}`);
    assert.equal(git(s.copy, 'rev-parse', 'HEAD'), head);
    assert.equal(existsSync(join(gitDir, 'hoshin', 'baseline-restore.json')), false);
  }
});

test('a baseline check that modifies a tracked file outside the app folder is not "back": the restore reports a problem', () => {
  const s = started({ hooks: { 'build.finish': [nodeHook('lock', `require('fs').appendFileSync('../../README.md', 'changed by a check')`)] } });
  const r = buildStart(s, 'api');
  assert.notEqual(r.json.outcome, 'passed', text(r));
  assert.match(text(r), /could not be put back on change\/2026-11-02-wave/);
  assert.match(text(r), /README\.md/);
  assert.equal(git(s.copy, 'branch', '--show-current'), `change/${NAME}`);
  assert.doesNotMatch(text(r), /Check out change\//);
  assert.match(text(r), /The checks changed tracked files there; restore or discard them/);
});
