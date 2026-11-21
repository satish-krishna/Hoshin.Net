// The commit command: every commit a skill makes, its header, scope, trailers, paths, check, and git failures.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { chmodSync, existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { CHANGE, INTENT, buildLog, dotnet, env, git, program, run, testsRoot, workspace, write } from './helpers/workspace.mjs';

const folder = `intent/demo/${CHANGE}`;
const examples = join(testsRoot, 'Examples');
const example = (name) => readFileSync(join(examples, 'change', '2', 'valid', name), 'utf8');
const REVIEW = (n) =>
  '```jsonc\n{ "layout": 2 }\n```\n\n# Review findings\n' +
  Array.from(
    { length: n },
    (_, i) => `\n## Review ${i + 1}\n\n\`\`\`jsonc\n{\n  "review": ${i + 1},\n  "reviewed_at": "2026-10-22",\n  "commits": { "app": "840fa0c" }\n}\n\`\`\`\n`
  ).join('');

const commitIn = (cwd, args, paths) => run(cwd, ['commit', '--json', ...args, '--', ...paths]);
const message = (dir) => git(dir, 'log', '-1', '--format=%B');
const header = (dir) => git(dir, 'log', '-1', '--format=%s');

function built(options = {}) {
  return workspace({ ...options, masterFiles: { [`${folder}/spec.md`]: example('spec.md'), [`${folder}/plan.md`]: example('plan.md'), [`${folder}/build/app.md`]: buildLog(['T1']), ...(options.masterFiles ?? {}) } });
}

test('a change-level kind commits only the paths given, with the change scope and the trailer', () => {
  const { master } = workspace();
  write(join(master, folder, 'spec.md'), example('spec.md'));
  write(join(master, 'other.txt'), 'staged by the engineer\n');
  git(master, 'add', 'other.txt');
  const r = commitIn(master, ['--kind', 'spec', '--change', CHANGE], [`${folder}/spec.md`]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.committed, true);
  assert.equal(r.json.header, `docs(demo): write spec ${CHANGE}`);
  assert.equal(r.json.repository, master);
  assert.equal(r.json.sha, git(master, 'rev-parse', 'HEAD'));
  assert.deepEqual(r.json.paths, [`${folder}/spec.md`]);
  assert.equal(r.json.reason, null);
  assert.deepEqual(r.json.problems, []);
  assert.equal(message(master), `docs(demo): write spec ${CHANGE}\n\nGenerated-By: hoshin`);
  assert.equal(git(master, 'status', '--porcelain'), 'A  other.txt');
});

test('every document kind has its header', () => {
  const kinds = [
    ['intent-draft', 'intent.md', `draft intent ${CHANGE}`],
    ['intent-reopen', 'intent.md', `reopen intent ${CHANGE}`],
    ['spec-reopen', 'spec.md', `reopen spec ${CHANGE}`],
    ['plan', 'plan.md', `write plan ${CHANGE}`],
    ['fix-draft', 'plan.md', `draft fix ${CHANGE}`],
    ['fix-reopen', 'plan.md', `reopen fix ${CHANGE}`],
    ['accept-intent', 'intent.md', `accept intent ${CHANGE}`],
    ['accept-spec', 'spec.md', `accept spec ${CHANGE}`],
    ['accept-plan', 'plan.md', `accept plan ${CHANGE}`],
    ['reject-intent', 'intent.md', `reject intent ${CHANGE}`],
    ['reject-fix', 'intent.md', `reject fix ${CHANGE}`],
  ];
  const { master } = built();
  for (const [kind, file, subject] of kinds) {
    const path = join(master, folder, file);
    writeFileSync(path, readFileSync(path, 'utf8') + `\n${kind}\n`);
    const r = commitIn(master, ['--kind', kind, '--change', CHANGE], [`${folder}/${file}`]);
    assert.equal(r.status, 0, `${kind}: ${r.stderr}${r.stdout}`);
    assert.equal(header(master), `docs(demo): ${subject}`);
  }
});

test('the change scope comes from the intent, then change.jsonc, then the system scope', () => {
  const intentWithScope = (scope) => {
    const fm = { title: 'Greeting', system: 'demo', author: 't', date: '2026-11-01', status: 'accepted', ...(scope ? { scope } : {}) };
    return '```jsonc\n' + JSON.stringify(fm, null, 2) + '\n```\n\n# Intent: Greeting\n';
  };
  const cases = [
    [{ masterFiles: { [`${folder}/intent.md`]: intentWithScope('PROJ-123') } }, 'PROJ-123'],
    [{ masterFiles: { [`${folder}/intent.md`]: intentWithScope(null), [`${folder}/change.jsonc`]: JSON.stringify({ version: 1, branch: 'change/x', type: 'feat', scope: 'rec-scope', base: 'main' }, null, 2) + '\n' } }, 'rec-scope'],
    [{ masterFiles: { [`${folder}/intent.md`]: intentWithScope(null) }, marker: { systems: { demo: { repos: ['app'], commitScope: 'greeter' } } } }, 'greeter'],
    [{ masterFiles: { [`${folder}/intent.md`]: intentWithScope(null) } }, 'demo'],
    [{ masterFiles: { [`${folder}/intent.md`]: intentWithScope(null) }, marker: { systems: { demo: { repos: ['app'], commitScope: 'Greeter_X' } } } }, 'demo'],
  ];
  for (const [options, scope] of cases) {
    const { master } = workspace(options);
    write(join(master, folder, 'notes.md'), 'x\n');
    const r = commitIn(master, ['--kind', 'intent-draft', '--change', CHANGE], [`${folder}/notes.md`]);
    assert.equal(r.status, 0, r.stderr + r.stdout);
    assert.equal(r.json.header, `docs(${scope}): draft intent ${CHANGE}`);
  }
});

test('setup and instructions use the system scope, with and without commitScope', () => {
  const plain = workspace();
  write(join(plain.master, 'intent/demo/policies.md'), '# Policies\n');
  let r = commitIn(plain.master, ['--kind', 'setup', '--system', 'demo'], ['intent/demo/policies.md']);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.header, 'chore(demo): set up hoshin for demo');

  const scoped = workspace({ marker: { systems: { demo: { repos: ['app'], commitScope: 'greeter' }, other: { repos: [] } } } });
  write(join(scoped.master, 'intent/workspace.md'), '# Workspace\n');
  r = commitIn(scoped.master, ['--kind', 'instructions'], ['intent/workspace.md']);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.header, 'chore(greeter): refresh workspace instructions');
  assert.equal(message(scoped.master), 'chore(greeter): refresh workspace instructions\n\nGenerated-By: hoshin');

  r = commitIn(plain.master, ['--kind', 'setup', '--system', 'nope'], ['intent/demo/policies.md']);
  assert.equal(r.status, 2);
});

test('setup checks the marker it commits, and refuses a broken one', () => {
  const { master } = workspace();
  write(join(master, '.hoshin.json'), JSON.stringify({ version: 1, systems: { demo: { repos: ['app'] } }, hooks: { 'no.such.event': [] } }) + '\n');
  const r = commitIn(master, ['--kind', 'setup', '--system', 'demo'], ['.hoshin.json']);
  assert.equal(r.status, 1, r.stderr + r.stdout);
  assert.equal(r.json.committed, false);
  assert.equal(r.json.reason, 'check failed');
  assert.ok(r.json.problems.length > 0 && r.json.problems.every((p) => /\.hoshin\.json:\d+: /.test(p)), r.stdout);
  assert.equal(header(master), 'chore: initial');
});

test('setup refuses a marker with an unknown field, a bad commitScope or no version', () => {
  for (const [marker, problem] of [
    [{ version: 1, systems: { demo: { repos: ['app'] } }, extra: true }, /unknown field extra/],
    [{ version: 1, systems: { demo: { repos: ['app'], commitScope: 'Bad Scope' } } }, /commitScope must be lowercase kebab case/],
    [{ systems: { demo: { repos: ['app'] } } }, /version must be 1, not missing/],
  ]) {
    const { master } = workspace();
    write(join(master, '.hoshin.json'), JSON.stringify(marker) + '\n');
    const r = commitIn(master, ['--kind', 'setup', '--system', 'demo'], ['.hoshin.json']);
    assert.equal(r.status, 1, r.stderr + r.stdout);
    assert.equal(r.json.reason, 'check failed');
    assert.ok(r.json.problems.some((p) => problem.test(p)), r.stdout);
    assert.equal(header(master), 'chore: initial');
  }
});

test('context builds, refreshes, and lists more than three repositories in the body', () => {
  const { master } = workspace();
  const ctx = (repo) => `intent/demo/context/${repo}.md`;
  for (const repo of ['app', 'api']) write(join(master, ctx(repo)), `# ${repo}\n`);
  let r = commitIn(master, ['--kind', 'context', '--system', 'demo'], [ctx('app'), ctx('api')]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.header, 'docs(demo): build context for app, api');

  for (const repo of ['app', 'api']) write(join(master, ctx(repo)), `# ${repo} again\n`);
  r = commitIn(master, ['--kind', 'context', '--system', 'demo'], [ctx('app'), ctx('api')]);
  assert.equal(r.json.header, 'docs(demo): refresh context for app, api');

  const many = ['app', 'api', 'web', 'worker'];
  for (const repo of many) write(join(master, ctx(repo)), `# ${repo} third\n`);
  r = commitIn(master, ['--kind', 'context', '--system', 'demo'], many.map(ctx));
  assert.equal(r.json.header, 'docs(demo): build context for 4 repositories');
  assert.equal(message(master), 'docs(demo): build context for 4 repositories\n\napp, api, web, worker\n\nGenerated-By: hoshin');
});

test('build-log names the entries the logs add against HEAD, after checking the documents', () => {
  const { master } = built();
  const log = `${folder}/build/app.md`;
  write(join(master, log), buildLog(['T1', 'T2']));
  let r = commitIn(master, ['--kind', 'build-log', '--change', CHANGE], [log]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.header, `docs(demo): log T2 of ${CHANGE}`);

  write(join(master, log), buildLog(['T1', 'T2', 'T3']));
  r = commitIn(master, ['--kind', 'build-log', '--change', CHANGE], [log]);
  assert.equal(r.json.header, `docs(demo): log T3 of ${CHANGE}`);
});

test('build-log names several added entries in document order', () => {
  const { master } = built();
  const log = `${folder}/build/app.md`;
  write(join(master, log), buildLog(['T1', 'T2', 'T3']));
  const r = commitIn(master, ['--kind', 'build-log', '--change', CHANGE], [log]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.header, `docs(demo): log T2, T3 of ${CHANGE}`);
});

test('a check failure commits and stages nothing', () => {
  const { master } = built();
  const log = `${folder}/build/app.md`;
  write(join(master, log), buildLog(['T1', 'T9']));
  const r = commitIn(master, ['--kind', 'build-log', '--change', CHANGE], [log]);
  assert.equal(r.status, 1, r.stderr + r.stdout);
  assert.equal(r.json.committed, false);
  assert.equal(r.json.reason, 'check failed');
  assert.ok(r.json.problems.some((p) => /app\.md:\d+: .*T9/.test(p)), r.stdout);
  assert.equal(header(master), 'chore: initial');
  assert.equal(git(master, 'diff', '--cached', '--name-only'), '');
});

test('review numbers the review by its sections', () => {
  const { master } = built();
  const review = `${folder}/build/review.md`;
  write(join(master, review), REVIEW(1));
  let r = commitIn(master, ['--kind', 'review', '--change', CHANGE], [review]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.header, `docs(demo): record review 1 of ${CHANGE}`);
  write(join(master, review), REVIEW(2));
  r = commitIn(master, ['--kind', 'review', '--change', CHANGE], [review]);
  assert.equal(r.json.header, `docs(demo): record review 2 of ${CHANGE}`);
});

test('nothing changed is not an error', () => {
  const { master } = built();
  const r = commitIn(master, ['--kind', 'plan', '--change', CHANGE], [`${folder}/plan.md`]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.committed, false);
  assert.equal(r.json.reason, 'nothing changed');
  assert.equal(r.json.sha, null);
  assert.deepEqual(r.json.paths, []);
});

test('only the paths git status lists go in', () => {
  const { master } = built();
  write(join(master, folder, 'plan.md'), example('plan.md') + '\nmore\n');
  const r = commitIn(master, ['--kind', 'plan', '--change', CHANGE], [`${folder}/spec.md`, join(master, folder, 'plan.md')]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.deepEqual(r.json.paths, [`${folder}/plan.md`]);
  assert.equal(git(master, 'show', '--name-only', '--format=', 'HEAD'), `${folder}/plan.md`);
});

test('a path outside the repository, a missing option or an unknown kind cannot run', () => {
  const { ws, master } = workspace();
  write(join(ws, 'app', 'x.md'), 'x\n');
  assert.equal(commitIn(master, ['--kind', 'spec', '--change', CHANGE], [join(ws, 'app', 'x.md')]).status, 2);
  assert.equal(commitIn(master, ['--kind', 'spec', '--change', CHANGE], ['../app/x.md']).status, 2);
  assert.equal(commitIn(master, ['--kind', 'spec'], [`${folder}/intent.md`]).status, 2);
  assert.equal(commitIn(master, ['--kind', 'nope', '--change', CHANGE], [`${folder}/intent.md`]).status, 2);
  assert.equal(commitIn(master, ['--kind', 'context'], ['intent/demo/context/app.md']).status, 2);
  assert.equal(run(master, ['commit', '--json', '--kind', 'spec', '--change', CHANGE]).status, 2);
  assert.equal(header(master), 'chore: initial');
});

test('a path relative to the repository is taken as such when the session is outside it', () => {
  const { ws, master } = workspace();
  write(join(master, folder, 'intent.md'), readFileSync(join(master, folder, 'intent.md'), 'utf8') + '\nMore.\n');
  const r = commitIn(ws, ['--kind', 'intent-draft', '--change', CHANGE, '--master', master], [`${folder}/intent.md`]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.deepEqual(r.json.paths, [`${folder}/intent.md`]);
  assert.equal(header(master), `docs(demo): draft intent ${CHANGE}`);
});

test('work commits in the product repository with both trailers in one paragraph', () => {
  const { app } = workspace();
  write(join(app, 'src/greet.js'), 'export const greet = () => "hi";\n');
  const r = commitIn(app, ['--kind', 'work', '--change', CHANGE, '--repo', 'app', '--task', 'T1', '--type', 'feat', '--subject', 'greet returning visitors'], ['src/greet.js']);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.repository, app);
  assert.equal(r.json.header, 'feat(demo): greet returning visitors');
  assert.equal(message(app), 'feat(demo): greet returning visitors\n\nGenerated-By: hoshin\nPlan-Task: T1');
  assert.equal(git(app, 'log', '-1', '--format=%(trailers:key=Plan-Task,valueonly)'), 'T1');
});

test('work without a task carries only the Generated-By trailer', () => {
  const { app } = workspace();
  write(join(app, 'src/greet.js'), 'export const greet = () => "hello";\n');
  const r = commitIn(app, ['--kind', 'work', '--change', CHANGE, '--repo', 'app', '--type', 'fix', '--subject', 'say hello'], ['src/greet.js']);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(message(app), 'fix(demo): say hello\n\nGenerated-By: hoshin');
});

test('--co-author adds a Co-Authored-By trailer after the hoshin trailers, in the same paragraph', () => {
  const { master, app } = workspace();
  const claude = 'Claude Opus 5.5 <noreply@anthropic.com>';
  write(join(app, 'src/greet.js'), 'export const greet = () => "hey";\n');
  const work = commitIn(app, ['--kind', 'work', '--change', CHANGE, '--repo', 'app', '--task', 'T1', '--type', 'feat', '--subject', 'greet', '--co-author', claude], ['src/greet.js']);
  assert.equal(work.status, 0, work.stderr + work.stdout);
  assert.equal(message(app), `feat(demo): greet\n\nGenerated-By: hoshin\nPlan-Task: T1\nCo-Authored-By: ${claude}`);
  assert.equal(git(app, 'log', '-1', '--format=%(trailers:key=Co-Authored-By,valueonly)'), claude);
  assert.equal(git(app, 'log', '-1', '--format=%(trailers:key=Generated-By,valueonly)'), 'hoshin');
  write(join(master, folder, 'spec.md'), example('spec.md'));
  const doc = commitIn(master, ['--kind', 'spec', '--change', CHANGE, '--co-author', claude], [`${folder}/spec.md`]);
  assert.equal(doc.status, 0, doc.stderr + doc.stdout);
  assert.equal(message(master), `docs(demo): write spec ${CHANGE}\n\nGenerated-By: hoshin\nCo-Authored-By: ${claude}`);
});

test('--co-author refuses a value that is not "Name <email>"', () => {
  const { app } = workspace();
  write(join(app, 'src/greet.js'), 'export const greet = () => "yo";\n');
  for (const bad of ['Claude', 'noreply@anthropic.com', '<noreply@anthropic.com>', 'Claude <not an email>', 'Claude <a@b.c>\nGenerated-By: other', '']) {
    const r = commitIn(app, ['--kind', 'work', '--change', CHANGE, '--repo', 'app', '--type', 'feat', '--subject', 'greet', '--co-author', bad], ['src/greet.js']);
    assert.equal(r.status, 2, `${JSON.stringify(bad)}: ${r.stdout}`);
  }
  assert.equal(header(app), 'chore: initial');
});

test('work refuses a repository the system does not list or that is not checked out, and a bad type or subject', () => {
  const { ws, app } = workspace({ repos: ['app', 'api'] });
  rmSync(join(ws, 'api'), { recursive: true, force: true });
  write(join(app, 'a.js'), 'a\n');
  const base = ['--kind', 'work', '--change', CHANGE, '--task', 'T1'];
  assert.equal(commitIn(app, [...base, '--repo', 'web', '--type', 'feat', '--subject', 's'], ['a.js']).status, 2);
  assert.equal(commitIn(app, [...base, '--repo', 'api', '--type', 'feat', '--subject', 's'], ['a.js']).status, 2);
  assert.equal(commitIn(app, [...base, '--repo', 'app', '--type', 'feature', '--subject', 's'], ['a.js']).status, 2);
  assert.equal(commitIn(app, [...base, '--repo', 'app', '--type', 'feat'], ['a.js']).status, 2);
  assert.equal(commitIn(app, [...base, '--repo', 'app', '--type', 'feat', '--subject', ' leading space'], ['a.js']).status, 2);
  assert.equal(header(app), 'chore: initial');
});

test('a failing commit-msg or pre-commit hook is reported verbatim and the files stay', () => {
  for (const hook of ['commit-msg', 'pre-commit']) {
    const { master } = workspace();
    const path = join(master, '.git', 'hooks', hook);
    write(path, `#!/bin/sh\necho "${hook} says no" >&2\nexit 1\n`);
    chmodSync(path, 0o755);
    write(join(master, folder, 'notes.md'), 'x\n');
    const r = commitIn(master, ['--kind', 'intent-draft', '--change', CHANGE], [`${folder}/notes.md`]);
    assert.equal(r.status, 1, r.stderr + r.stdout);
    assert.equal(r.json.committed, false);
    assert.equal(r.json.reason, 'git refused');
    assert.ok(r.json.problems.some((p) => p.includes(`${hook} says no`)), r.stdout);
    assert.equal(readFileSync(join(master, folder, 'notes.md'), 'utf8'), 'x\n');
    assert.equal(header(master), 'chore: initial');
  }
});

test("the change's copy is chosen over the master", () => {
  const { ws, master } = workspace();
  const started = run(master, ['change', 'start', '--json', '--system', 'demo', '--change', '2026-11-02-copy', '--type', 'feat', '--scope', 'copyscope']);
  assert.equal(started.status, 0, started.stderr + started.stdout);
  const copy = join(ws, '.hoshin', 'copies', '2026-11-02-copy');
  const docs = 'intent/demo/2026-11-02-copy';
  write(join(copy, docs, 'notes.md'), 'x\n');
  const r = commitIn(copy, ['--kind', 'intent-draft', '--change', '2026-11-02-copy'], [`${docs}/notes.md`]);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(r.json.repository, copy);
  assert.equal(r.json.header, 'docs(copyscope): draft intent 2026-11-02-copy');
  assert.equal(header(master), 'chore: initial');
  write(join(master, 'stray.md'), 'x\n');
  assert.equal(commitIn(master, ['--kind', 'intent-draft', '--change', '2026-11-02-copy'], ['stray.md']).status, 2);
});

test('a held index.lock is waited for, then the commit goes in', async () => {
  const { master } = workspace();
  write(join(master, folder, 'notes.md'), 'x\n');
  const lock = join(master, '.git', 'index.lock');
  writeFileSync(lock, '');
  setTimeout(() => rmSync(lock, { force: true }), 3000);
  const r = await new Promise((done) => {
    const child = spawn(dotnet, [program, 'commit', '--json', '--kind', 'intent-draft', '--change', CHANGE, '--', `${folder}/notes.md`], { cwd: master, env });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (d) => (stdout += d));
    child.stderr.on('data', (d) => (stderr += d));
    child.on('close', (status) => done({ status, stdout, stderr }));
  });
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.equal(JSON.parse(r.stdout).committed, true);
  assert.equal(existsSync(lock), false);
  assert.equal(header(master), `docs(demo): draft intent ${CHANGE}`);
});

test('the guard lets the commit command through', () => {
  for (const command of [
    `dotnet /p/hoshin.dll commit --json --kind review --change ${CHANGE} -- intent/demo/${CHANGE}/build/review.md`,
    'dotnet /home/u/hoshin/hoshin.dll commit --json --kind setup --system demo -- .hoshin.json intent/demo/hooks.json',
    `dotnet /p/hoshin.dll commit --json --kind work --change ${CHANGE} --repo app --task T1 --type feat --subject "add the greeting" -- src/greet.js`,
  ]) {
    const r = spawnSync(dotnet, [program, 'guard'], { input: JSON.stringify({ hook_event_name: 'PreToolUse', tool_name: 'Bash', tool_input: { command } }), encoding: 'utf8', env });
    assert.equal(r.stdout, '', command);
  }
});
