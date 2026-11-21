// A change's branches and its copy of the master: change start, build.start, change rename, change remove, change show.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, realpathSync, renameSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { CHANGE, buildLog, commit, git, nodeHook, run, workspace, write } from './helpers/workspace.mjs';

const NEW = '2026-11-02-hello';
const start = (cwd, ...more) => run(cwd, ['change', 'start', '--json', '--system', 'demo', '--change', NEW, '--type', 'feat', '--scope', 'PROJ-7', ...more]);
const copyOf = (ws, change = NEW) => join(ws, '.hoshin', 'copies', change);
const parseJsonc = (text) => JSON.parse(text.replace(/^\/\/.*\n/gm, ''));

test("change start creates the branch off the base, the change's copy on it, and commits change.jsonc there", () => {
  const { ws, master } = workspace();
  git(master, 'checkout', '-q', '-b', 'my-own-work');
  const r = start(master);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.outcome, 'passed');
  const copy = copyOf(ws);
  assert.deepEqual(r.json.details, { branch: `change/${NEW}`, type: 'feat', base: 'main', copy, created: true });
  assert.equal(git(master, 'branch', '--show-current'), 'my-own-work');
  assert.equal(git(copy, 'branch', '--show-current'), `change/${NEW}`);
  assert.equal(git(copy, 'merge-base', 'HEAD', 'main'), git(master, 'rev-parse', 'main'));
  assert.equal(git(copy, 'log', '-1', '--format=%s%n%b'), `docs(PROJ-7): start change ${NEW}\nGenerated-By: hoshin`);
  const record = parseJsonc(readFileSync(join(copy, 'intent', 'demo', NEW, 'change.jsonc'), 'utf8'));
  assert.equal(record.branch, `change/${NEW}`);
  assert.equal(record.type, 'feat');
  assert.equal(record.base, 'main');
  assert.equal(record.scope, 'PROJ-7');
  assert.equal(run(copy, ['check', join(copy, 'intent', 'demo', NEW, 'change.jsonc')]).status, 0);
  const resolved = run(copy, ['resolve', '--json']);
  assert.equal(resolved.json.masterIsCopy, true);
  assert.equal(realpathSync(resolved.json.workspace), ws);
  const again = start(master);
  assert.equal(again.json.details.created, false);
});

test('the team naming committed on the base branch names the branch; the working marker does not', () => {
  const naming = { branch: '{type}/{scope}-{slug}', pullRequestTitle: '{type}({scope}): {title}' };
  const { ws, master } = workspace({ marker: { systems: { demo: { repos: ['app'], naming } } } });
  const marker = JSON.parse(readFileSync(join(master, '.hoshin.json'), 'utf8'));
  marker.systems.demo.naming.branch = 'sneaky/{slug}';
  write(join(master, '.hoshin.json'), JSON.stringify(marker));
  const r = start(master, '--title', 'Say hello');
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.json.details.branch, 'feat/PROJ-7-hello');
  assert.equal(git(copyOf(ws), 'branch', '--show-current'), 'feat/PROJ-7-hello');
});

test('a branch name that is invalid, taken ignoring capitals, or a folder of another branch is refused before anything is made', () => {
  const { ws, master, app } = workspace();
  git(app, 'branch', 'Feat/Hello');
  git(master, 'branch', 'release');
  for (const [name, want] of [
    ['feat/hello', /feat\/hello is taken in app by the branch Feat\/Hello/],
    ['release/next', /clashes in master with the branch release/],
    ['bad name', /cannot contain a space/],
  ]) {
    const r = start(master, '--branch', name);
    assert.equal(r.status, 1, name);
    assert.equal(r.json.outcome, 'refused');
    assert.match(r.json.problems.join('\n'), want);
  }
  assert.equal(existsSync(copyOf(ws)), false);
});

test('a bad change name, work type or scope is refused', () => {
  const { master } = workspace();
  const r = run(master, ['change', 'start', '--json', '--system', 'demo', '--change', 'Hello', '--type', 'feature', '--scope', 'has space']);
  assert.equal(r.status, 1);
  const all = r.json.problems.join('\n');
  assert.match(all, /Hello is not a change name/);
  assert.match(all, /the work type must be one of feat, fix/);
  assert.match(all, /the scope: /);
});

test('a failed before hook of change.start makes nothing, and an after hook runs in the copy', () => {
  const where = 'require("fs").writeFileSync(process.env.HOSHIN_MASTER + "/../where.txt", process.cwd())';
  const blocked = workspace({ marker: { hooks: { 'change.start': [nodeHook('ticket', 'process.exit(1)')] } } });
  const r = start(blocked.master);
  assert.equal(r.json.outcome, 'failed');
  assert.equal(existsSync(copyOf(blocked.ws)), false);
  assert.equal(git(blocked.master, 'branch', '--list', `change/${NEW}`), '');
  const ok = workspace({ marker: { hooks: { 'change.start': [nodeHook('where', where, { when: 'after' })] } } });
  assert.equal(start(ok.master).json.outcome, 'passed');
  assert.equal(readFileSync(join(ok.ws, '.hoshin', 'copies', 'where.txt'), 'utf8'), copyOf(ok.ws));
});

test('build.start puts a product repository on the change branch, and a second run reuses it', () => {
  const { ws, master, app } = workspace();
  start(master);
  const copy = copyOf(ws);
  const r = run(app, ['run', 'build.start', '--json', '--change', NEW, '--master', copy]);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.deepEqual(r.json.details, { branch: `change/${NEW}`, created: true, uncommitted: [], from: 'main' });
  assert.equal(git(app, 'branch', '--show-current'), `change/${NEW}`);
  const active = JSON.parse(readFileSync(join(app, '.git', 'hoshin', 'active-build.json'), 'utf8'));
  assert.deepEqual([active.change, active.repo, active.system], [NEW, 'app', 'demo']);
  commit(app, { 'hello.txt': 'hi' }, 'feat(PROJ-7): say hello');
  const again = run(app, ['run', 'build.start', '--json', '--change', NEW, '--master', copy]);
  assert.equal(again.json.outcome, 'passed', JSON.stringify(again.json));
  assert.equal(again.json.details.created, false);
});

test('the change is found in its copy from a product repository with no --master', () => {
  const { master, app } = workspace();
  start(master);
  const r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(r.json.outcome, 'passed', r.stderr);
});

test("build.start on the change's branch passes with uncommitted files and names them", () => {
  const { master, app } = workspace();
  start(master);
  assert.equal(run(app, ['run', 'build.start', '--json', '--change', NEW]).json.outcome, 'passed');
  write(join(app, 'half-done.txt'), 'x');
  const r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(r.status, 0, JSON.stringify(r.json));
  assert.equal(r.json.outcome, 'passed');
  assert.deepEqual(r.json.details.uncommitted, ['half-done.txt']);
  assert.equal(r.json.details.created, false);
  assert.equal(git(app, 'branch', '--show-current'), `change/${NEW}`);
});

test("build.start in the master listed as a product repository leaves hoshin's own files out of uncommitted files", () => {
  const { ws, master } = workspace({ marker: { systems: { demo: { repos: ['app', 'hub'] } } } });
  const hub = join(ws, 'hub');
  renameSync(master, hub);
  write(join(hub, 'intent', 'demo', CHANGE, 'notes.md'), 'being written by another run');
  write(join(hub, '.hoshin.json'), readFileSync(join(hub, '.hoshin.json'), 'utf8') + '\n');
  let r = run(hub, ['run', 'build.start', '--json', '--change', CHANGE, '--repo', 'hub']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  assert.deepEqual(r.json.details.uncommitted, []);
  assert.equal(git(hub, 'branch', '--show-current'), `change/${CHANGE}`);
  write(join(hub, 'README.md'), 'edited');
  r = run(hub, ['run', 'build.start', '--json', '--change', CHANGE, '--repo', 'hub']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  assert.deepEqual(r.json.details.uncommitted, ['README.md']);
  git(hub, 'checkout', '-q', 'main');
  r = run(hub, ['run', 'build.start', '--json', '--change', CHANGE, '--repo', 'hub']);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems[0], /hub has uncommitted files: README\.md\./);
});

test("build.start refuses uncommitted files on the base, another branch, and a branch holding work that is not the change's", () => {
  const { master, app } = workspace();
  start(master);
  write(join(app, 'stray.txt'), 'x');
  let r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems[0], /uncommitted files: stray\.txt/);
  assert.equal(git(app, 'branch', '--show-current'), 'main');
  git(app, 'clean', '-fq');
  git(app, 'checkout', '-q', '-b', 'elsewhere');
  r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.match(r.json.problems[0], /app is on elsewhere, not on its base branch main/);
  git(app, 'checkout', '-q', 'main');
  git(app, 'checkout', '-q', '-b', `change/${NEW}`);
  commit(app, { 'other.txt': 'x' }, 'feat(other): unrelated');
  git(app, 'checkout', '-q', 'main');
  r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems[0], /work that is not this change's/);
  assert.match(r.json.problems[0], /feat\(other\): unrelated: its header does not carry the scope PROJ-7/);
  assert.equal(git(app, 'branch', '--show-current'), 'main');
});

test('run build.start --check checks every named repository, passes only if all pass, names each that refuses, and changes none', () => {
  const { ws, master, app } = workspace({ repos: ['app', 'web', 'api'] });
  start(master);
  const web = join(ws, 'web');
  const ok = run(master, ['run', 'build.start', '--json', '--change', NEW, '--check', '--repo', 'app', '--repo', 'api']);
  assert.equal(ok.status, 0, JSON.stringify(ok.json) + ok.stderr);
  assert.equal(ok.json.outcome, 'passed');
  assert.equal(ok.json.check, true);
  assert.deepEqual(ok.json.repos.map((r) => [r.repo, r.outcome, r.problems]), [['app', 'passed', []], ['api', 'passed', []]]);
  assert.deepEqual(ok.json.refusing, []);
  write(join(web, 'stray.txt'), 'x');
  git(join(ws, 'api'), 'checkout', '-q', '-b', 'elsewhere');
  const r = run(master, ['run', 'build.start', '--json', '--change', NEW, '--check', '--repo', 'app', '--repo', 'web', '--repo', 'api']);
  assert.equal(r.status, 1);
  assert.equal(r.json.outcome, 'refused');
  assert.deepEqual(r.json.refusing, ['web', 'api']);
  assert.deepEqual(r.json.repos.map((x) => [x.repo, x.outcome]), [['app', 'passed'], ['web', 'refused'], ['api', 'refused']]);
  assert.match(r.json.problems.join('\n'), /web has uncommitted files: stray\.txt/);
  assert.match(r.json.problems.join('\n'), /api is on elsewhere/);
  assert.equal(git(app, 'branch', '--show-current'), 'main');
  assert.equal(git(app, 'branch', '--list', `change/${NEW}`), '');
  assert.equal(existsSync(join(app, '.git', 'hoshin', 'active-build.json')), false);
  const status = run(master, ['status', '--json', '--change', NEW]);
  assert.deepEqual(status.json.repos.flatMap((x) => x.runs).filter((e) => e.event === 'build.start'), []);
  const unknown = run(master, ['run', 'build.start', '--json', '--change', NEW, '--check', '--repo', 'app', '--repo', 'nope']);
  assert.equal(unknown.status, 2);
  assert.deepEqual(unknown.json.repos.map((x) => [x.repo, x.outcome]), [['app', 'passed'], ['nope', 'could-not-run']]);
  assert.match(unknown.json.problems.join('\n'), /nope is not a repository of system demo/);
  const twice = run(master, ['run', 'build.start', '--json', '--change', NEW, '--repo', 'app', '--repo', 'web']);
  assert.equal(twice.status, 2);
});

test('run --check runs no hook', () => {
  const { master, app } = workspace({ marker: { hooks: { 'build.start': [nodeHook('loud', 'require("fs").writeFileSync("ran.txt", "x")')] } } });
  start(master);
  const r = run(app, ['run', 'build.start', '--json', '--change', NEW, '--check']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  assert.deepEqual(r.json.repos.map((x) => x.repo), ['app']);
  assert.equal(existsSync(join(app, 'ran.txt')), false);
});

test("build.start switches a clean repository off another change's branch once that build has finished at its head", () => {
  const { master, app } = workspace({ built: true });
  assert.equal(run(app, ['run', 'build.start', '--json', '--change', CHANGE, '--master', master]).json.outcome, 'passed');
  start(master);
  let r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems[0], /app is on change\/2026-11-01-greeting, not on its base branch main or the change's branch change\/2026-11-02-hello/);
  assert.match(r.json.problems[0], /it is the branch of 2026-11-01-greeting, whose build is active here and has not finished at its head/);
  assert.equal(run(app, ['run', 'build.finish', '--json', '--change', CHANGE, '--master', master]).json.outcome, 'passed');
  write(join(app, 'stray.txt'), 'x');
  r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems[0], /app has uncommitted files: stray\.txt/);
  assert.equal(git(app, 'branch', '--show-current'), `change/${CHANGE}`);
  git(app, 'clean', '-fq');
  r = run(app, ['run', 'build.start', '--json', '--change', NEW, '--check']);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(r.status, 0, JSON.stringify(r.json));
  assert.deepEqual(r.json.details, { branch: `change/${NEW}`, created: true, uncommitted: [], from: `change/${CHANGE}` });
  assert.equal(git(app, 'branch', '--show-current'), `change/${NEW}`);
  assert.equal(git(app, 'merge-base', 'HEAD', 'main'), git(app, 'rev-parse', 'main'));
  assert.equal(git(app, 'rev-parse', 'HEAD'), git(app, 'rev-parse', 'main'));
});

test("build.start switches off another change's branch that no active build names, and never off a branch that is no change's", () => {
  const { master, app } = workspace({ built: true });
  start(master);
  const r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(r.status, 0, JSON.stringify(r.json));
  assert.equal(r.json.details.from, `change/${CHANGE}`);
  assert.equal(git(app, 'branch', '--show-current'), `change/${NEW}`);
  git(app, 'checkout', '-q', '-b', 'engineer-work', 'main');
  const own = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(own.json.outcome, 'refused');
  assert.match(own.json.problems[0], /app is on engineer-work, not on its base branch main or the change's branch change\/2026-11-02-hello/);
});

test("build.start does not switch off another change's branch after a commit its checks have not seen", () => {
  const { master, app } = workspace({ built: true });
  run(app, ['run', 'build.start', '--json', '--change', CHANGE, '--master', master]);
  assert.equal(run(app, ['run', 'build.finish', '--json', '--change', CHANGE, '--master', master]).json.outcome, 'passed');
  commit(app, { 'later.txt': 'x' }, 'fix(demo): later');
  start(master);
  const r = run(app, ['run', 'build.start', '--json', '--change', NEW]);
  assert.equal(r.json.outcome, 'refused');
  assert.match(r.json.problems[0], /it is the branch of 2026-11-01-greeting, whose build is active here and has not finished at its head/);
  assert.equal(git(app, 'branch', '--show-current'), `change/${CHANGE}`);
});

test('a change started before change.jsonc keeps its branch: branch.md, else change/<change>', () => {
  const { master, app } = workspace();
  let r = run(app, ['run', 'build.start', '--json', '--change', CHANGE, '--master', master]);
  assert.equal(r.json.outcome, 'passed', JSON.stringify(r.json));
  assert.equal(git(app, 'branch', '--show-current'), `change/${CHANGE}`);
  git(app, 'checkout', '-q', 'main');
  commit(master, { [`intent/demo/${CHANGE}/branch.md`]: '```jsonc\n{ "branch": "feat/old-style", "type": "feat" }\n```\n' });
  r = run(app, ['run', 'build.start', '--json', '--change', CHANGE, '--master', master]);
  assert.equal(git(app, 'branch', '--show-current'), 'feat/old-style');
});

test('change rename moves the branch in the master, the copy and every product repository, and records it', () => {
  const { ws, master, app } = workspace();
  start(master);
  const copy = copyOf(ws);
  run(app, ['run', 'build.start', '--json', '--change', NEW]);
  git(app, 'branch', 'taken');
  const clash = run(copy, ['change', 'rename', '--json', '--change', NEW, '--to', 'TAKEN']);
  assert.equal(clash.status, 1);
  assert.match(clash.json.problems.join('\n'), /TAKEN is taken in app/);
  const r = run(copy, ['change', 'rename', '--json', '--change', NEW, '--to', 'feat/PROJ-7-hello']);
  assert.equal(r.status, 0, JSON.stringify(r.json));
  assert.deepEqual(r.json.renamed, ['master', 'app']);
  assert.equal(git(copy, 'branch', '--show-current'), 'feat/PROJ-7-hello');
  assert.equal(git(app, 'branch', '--show-current'), 'feat/PROJ-7-hello');
  assert.equal(git(copy, 'log', '-1', '--format=%s'), `docs(PROJ-7): rename branch ${NEW}`);
  const shown = run(copy, ['change', 'show', '--json', '--change', NEW]);
  assert.equal(shown.json.branch, 'feat/PROJ-7-hello');
  assert.equal(shown.json.record.type, 'feat');
});

test('change remove takes away the copy and keeps a branch git does not see as merged', () => {
  const { ws, master } = workspace();
  start(master);
  const r = run(master, ['change', 'remove', '--json', '--change', NEW]);
  assert.equal(r.status, 0, JSON.stringify(r.json));
  assert.equal(existsSync(copyOf(ws)), false);
  assert.deepEqual(r.json.removed, ['copy']);
  assert.match(r.json.kept.join('\n'), /master: change\/2026-11-02-hello/);
});

test('change remove refuses a copy with uncommitted files, and removes nothing', () => {
  for (const [what, dirty] of [['untracked', (copy) => write(join(copy, 'draft.md'), 'x')], ['modified', (copy) => write(join(copy, 'README.md'), 'edited\n')]]) {
    const { ws, master } = workspace();
    start(master);
    git(master, 'merge', '-q', '--ff-only', `change/${NEW}`);
    dirty(copyOf(ws));
    const r = run(master, ['change', 'remove', '--json', '--change', NEW]);
    assert.equal(r.status, 1, what);
    assert.match(r.json.problems.join('\n'), /the change's copy has uncommitted files: (draft\.md|README\.md)/, what);
    assert.deepEqual([r.json.removed, r.json.kept], [[], []], what);
    assert.equal(existsSync(copyOf(ws)), true, `${what}: the copy was removed`);
    assert.notEqual(git(master, 'branch', '--list', `change/${NEW}`), '', `${what}: the merged branch was deleted`);
  }
});

test('change remove --abandon drops a rejected change but keeps the master branch that records the rejection', () => {
  const { ws, master, app } = workspace();
  start(master);
  const copy = copyOf(ws);
  const intentFile = `intent/demo/${NEW}/intent.md`;
  const intentJsonc = (status) => '```jsonc\n' + JSON.stringify({ title: 'Hello', system: 'demo', author: 't', date: '2026-11-02', status, scope: 'PROJ-7' }, null, 2) + '\n```\n\n# Intent: Hello\n';
  commit(copy, { [intentFile]: intentJsonc('draft') }, `docs(PROJ-7): draft intent ${NEW}`);
  run(app, ['run', 'build.start', '--json', '--change', NEW]);
  git(app, 'checkout', '-q', 'main');
  let r = run(master, ['change', 'remove', '--json', '--change', NEW, '--abandon']);
  assert.equal(r.status, 1);
  write(join(copy, intentFile), intentJsonc('rejected'));
  r = run(master, ['change', 'remove', '--json', '--change', NEW, '--abandon']);
  assert.equal(r.status, 1);
  assert.equal(run(copy, ['commit', '--json', '--kind', 'reject-intent', '--change', NEW, '--', intentFile]).json.committed, true);
  r = run(ws, ['change', 'remove', '--json', '--change', NEW, '--abandon']);
  assert.equal(r.status, 0, JSON.stringify(r.json) + r.stderr);
  assert.deepEqual(r.json, { change: NEW, problems: [], removed: ['copy', `app: change/${NEW}`], kept: [`master: change/${NEW} (it records the rejected intent)`] });
  assert.equal(existsSync(copy), false);
  assert.equal(git(master, 'log', '-1', '--format=%s', `change/${NEW}`), `docs(PROJ-7): reject intent ${NEW}`);
  assert.equal(git(app, 'branch', '--list', `change/${NEW}`), '');
});

test('change remove --abandon still refuses a rejected change with work in a product repository', () => {
  const { ws, master, app } = workspace();
  start(master);
  const copy = copyOf(ws);
  const intentFile = `intent/demo/${NEW}/intent.md`;
  const rejected = '```jsonc\n' + JSON.stringify({ title: 'Hello', system: 'demo', author: 't', date: '2026-11-02', status: 'rejected', scope: 'PROJ-7' }, null, 2) + '\n```\n\n# Intent: Hello\n';
  commit(copy, { [intentFile]: rejected }, `docs(PROJ-7): reject intent ${NEW}`);
  run(app, ['run', 'build.start', '--json', '--change', NEW]);
  commit(app, { 'hello.txt': 'hi' }, 'feat(PROJ-7): say hello');
  git(app, 'checkout', '-q', 'main');
  const r = run(master, ['change', 'remove', '--json', '--change', NEW, '--abandon']);
  assert.equal(r.status, 1);
  assert.match(r.json.problems.join('\n'), /app's change branch change\/2026-11-02-hello has commits beyond main: feat\(PROJ-7\): say hello/);
  assert.equal(existsSync(copy), true);
});

test('change show prints every repository of the system with its branch and base', () => {
  const { ws, master, app } = workspace({ repos: ['app', 'web'] });
  start(master);
  let r = run(master, ['change', 'show', '--json', '--change', NEW]);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.json.repos, {
    app: { path: app, checkedOut: true, branch: 'main', base: 'main' },
    web: { path: join(ws, 'web'), checkedOut: true, branch: 'main', base: 'main' },
  });
  run(app, ['run', 'build.start', '--json', '--change', NEW]);
  git(join(ws, 'web'), 'checkout', '-q', '--detach');
  r = run(master, ['change', 'show', '--json', '--change', NEW]);
  assert.equal(r.json.repos.app.branch, `change/${NEW}`);
  assert.equal(r.json.repos.web.branch, null);
});

test('change show names a repository that is not checked out', () => {
  const { ws, master } = workspace({ repos: ['app', 'missing'] });
  rmSync(join(ws, 'missing'), { recursive: true, force: true });
  start(master);
  const r = run(master, ['change', 'show', '--json', '--change', NEW]);
  assert.deepEqual(r.json.repos.missing, { path: join(ws, 'missing'), checkedOut: false, branch: null, base: null });
});

test('change remove --abandon drops a change nothing was done in: its copy and its branches', () => {
  const { ws, master, app } = workspace();
  start(master);
  run(app, ['run', 'build.start', '--json', '--change', NEW]);
  git(app, 'checkout', '-q', 'main');
  const r = run(master, ['change', 'remove', '--json', '--change', NEW, '--abandon']);
  assert.equal(r.status, 0, JSON.stringify(r.json) + r.stderr);
  assert.deepEqual(r.json, { change: NEW, problems: [], removed: ['copy', `master: change/${NEW}`, `app: change/${NEW}`], kept: [] });
  assert.equal(existsSync(copyOf(ws)), false);
  assert.equal(git(master, 'branch', '--list', `change/${NEW}`), '');
  assert.equal(git(app, 'branch', '--list', `change/${NEW}`), '');
});

test("change remove --abandon counts a start commit that carries Generated-By: ai-sdlc as the change's own", () => {
  const { ws, master, app } = workspace();
  start(master);
  const copy = copyOf(ws);
  git(copy, 'commit', '-q', '--amend', '-m', `docs(PROJ-7): start change ${NEW}`, '-m', 'Generated-By: ai-sdlc');
  run(app, ['run', 'build.start', '--json', '--change', NEW]);
  git(app, 'checkout', '-q', 'main');
  const r = run(master, ['change', 'remove', '--json', '--change', NEW, '--abandon']);
  assert.equal(r.status, 0, JSON.stringify(r.json) + r.stderr);
  assert.equal(existsSync(copy), false);
});

test('change remove --abandon refuses a change with work in it, and removes nothing', () => {
  const refusals = [
    {
      what: 'a document committed in the copy',
      setup: ({ ws }) => commit(copyOf(ws), { [`intent/demo/${NEW}/intent.md`]: '# Intent\n' }),
      problem: /the master's change branch change\/2026-11-02-hello has commits beyond main: docs\(demo\): change/,
    },
    {
      what: 'a commit in a product repository',
      setup: ({ app }) => {
        run(app, ['run', 'build.start', '--json', '--change', NEW]);
        commit(app, { 'hello.txt': 'hi' }, 'feat(PROJ-7): say hello');
        git(app, 'checkout', '-q', 'main');
      },
      problem: /app's change branch change\/2026-11-02-hello has commits beyond main: feat\(PROJ-7\): say hello/,
    },
    {
      what: 'a product repository still on the change branch',
      setup: ({ app }) => run(app, ['run', 'build.start', '--json', '--change', NEW]),
      problem: /app has change\/2026-11-02-hello checked out/,
    },
    {
      what: 'uncommitted files in the copy',
      setup: ({ ws }) => write(join(copyOf(ws), 'draft.md'), 'x'),
      problem: /the change's copy has uncommitted files: draft\.md/,
    },
    {
      what: 'a pull request in change.jsonc',
      setup: ({ ws }) => {
        const file = join(copyOf(ws), 'intent', 'demo', NEW, 'change.jsonc');
        const rec = parseJsonc(readFileSync(file, 'utf8'));
        rec.pullRequests = [{ repo: 'app', url: 'https://github.com/o/app/pull/1', number: 1, pushed: 'abcdef1' }];
        write(file, JSON.stringify(rec, null, 2) + '\n');
        git(copyOf(ws), 'commit', '-q', '-a', '--amend', '--no-edit');
      },
      problem: /change\.jsonc records a pull request: https:\/\/github\.com\/o\/app\/pull\/1/,
    },
  ];
  for (const row of refusals) {
    const w = workspace();
    start(w.master);
    row.setup(w);
    const r = run(w.master, ['change', 'remove', '--json', '--change', NEW, '--abandon']);
    assert.equal(r.status, 1, row.what);
    assert.match(r.json.problems.join('\n'), row.problem, row.what);
    assert.deepEqual([r.json.removed, r.json.kept], [[], []], row.what);
    assert.equal(existsSync(copyOf(w.ws)), true, `${row.what}: the copy was removed`);
    assert.notEqual(git(w.master, 'branch', '--list', `change/${NEW}`), '', `${row.what}: the branch was deleted`);
  }
});

test('check refuses a malformed change.jsonc', () => {
  const { master } = workspace();
  write(join(master, 'intent/demo', CHANGE, 'change.jsonc'), JSON.stringify({ version: 1, branch: 'x', type: 'feature', extra: 1 }, null, 2) + '\n');
  const r = run(master, ['check', join(master, 'intent/demo', CHANGE, 'change.jsonc')]);
  assert.equal(r.status, 1);
  assert.match(r.stdout, /change\.jsonc:1: unknown field extra/);
  assert.match(r.stdout, /change\.jsonc:1: type must be one of/);
  assert.match(r.stdout, /change\.jsonc:1: base must be/);
});

test('a change in both the master and its own copy is read from the copy, by change show and by run', () => {
  const { ws, master } = workspace();
  start(master);
  const record = readFileSync(join(copyOf(ws), 'intent', 'demo', NEW, 'change.jsonc'), 'utf8');
  commit(master, { [`intent/demo/${NEW}/change.jsonc`]: record.replace('"type": "feat"', '"type": "fix"') });
  const shown = run(master, ['change', 'show', '--json', '--change', NEW]);
  assert.equal(shown.status, 0, shown.stderr);
  assert.equal(shown.json.record.type, 'feat');

  const built = workspace({ built: true });
  const copy = copyOf(built.ws, CHANGE);
  git(built.master, 'worktree', 'add', '-q', '-b', `change/${CHANGE}`, copy);
  commit(copy, { [`intent/demo/${CHANGE}/build/app.md`]: buildLog(['T1']) });
  const r = run(built.app, ['run', 'build.finish', '--json', '--change', CHANGE]);
  assert.match(JSON.stringify(r.json), /tasks without a build log entry: T2, T3/);
});
