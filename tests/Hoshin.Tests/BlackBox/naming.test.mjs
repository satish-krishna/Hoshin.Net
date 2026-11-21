import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { bareEnv, dotnet, program } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

function run(args) {
  const result = spawnSync(dotnet, [program, ...args], {
    cwd: tmpdir(),
    env: bareEnv,
    encoding: 'utf8',
  });
  return { status: result.status, out: result.stdout ? JSON.parse(result.stdout) : null, stderr: result.stderr };
}

function marker(naming) {
  const dir = tempDir('hoshin-naming-');
  const file = join(dir, '.hoshin.json');
  const kata = { repos: ['kata', 'hoshin'] };
  if (naming !== undefined) kata.naming = naming;
  writeFileSync(file, JSON.stringify({ systems: { kata } }));
  return file;
}

const naming = (setting, extra = []) => run(['naming', '--json', marker(setting), 'kata', ...extra]);
const branchName = (name) => run(['branch-name', '--json', name]);
const scope = (value) => run(['scope', '--json', value]);

const TITLE = '{type}({scope}): {title}';

test('naming refuses a setting with one pattern (R1.3)', () => {
  const r = naming({ branch: '{type}/{scope}-{slug}' });
  assert.equal(r.status, 1, r.stderr);
  assert.equal(r.out.declared, true);
  assert.deepEqual(r.out.problems, ['a naming setting needs both a branch pattern and a pull request title pattern']);
  assert.equal(r.out.names, undefined);
});

const R31 = [
  ['{type}/{scope} {slug}', 'a branch name cannot contain a space'],
  ['{type}/{ticket}-{slug}', '"ticket" is not a known part of a name'],
  ['{type}//{slug}', 'a branch name cannot contain an empty path segment'],
  ['{type}/{title}', 'the title can only be used in the pull request title'],
  ['change/pending-{slug}', 'names starting "change/pending-" are reserved for the tool driving the build'],
  ['{type}/{slug}.lock', 'a branch name cannot end in ".lock"'],
];

test('naming refuses each unusable branch pattern (R3.1)', () => {
  for (const [pattern, reason] of R31) {
    const r = naming({ branch: pattern, pullRequestTitle: TITLE });
    assert.equal(r.status, 1, pattern);
    assert.ok(r.out.problems.some((p) => p.includes(reason)), `${pattern}: ${JSON.stringify(r.out.problems)}`);
  }
});

test('branch-name refuses each unsafe name (R12.1)', () => {
  for (const name of ['-D', 'feat/../main', 'feat/@{1}']) {
    const r = branchName(name);
    assert.equal(r.status, 1, name);
    assert.ok(r.out.problems.length >= 1, name);
  }
  assert.deepEqual(branchName('-D').out.problems, ['a branch name cannot start with -']);
  const viaNaming = naming({ branch: '-f/{slug}', pullRequestTitle: TITLE });
  assert.equal(viaNaming.status, 1);
  assert.ok(viaNaming.out.problems.some((p) => p.includes('a branch name cannot start with -')));
});

const R52 = [
  ['PROJ-123', null],
  ['proj_123', null],
  ['v2.1', null],
  ['kata', null],
  ['PROJ 123', 'a scope cannot contain a space'],
  ['-PROJ', 'a scope must start with a letter or a digit'],
  ['PROJ(1)', 'a scope cannot contain parentheses'],
  ['PROJ/123', 'a scope can hold only letters, digits, "-", "_" and "."'],
  ['PROJ..123', 'a scope cannot contain ".."'],
  ['HEAD', 'a scope cannot be "HEAD"'],
  ['PROJ-1.', 'a scope cannot end in "."'],
  ['v2.', 'a scope cannot end in "."'],
  ['a.lock', 'a scope cannot end in ".lock"'],
];

test('scope accepts and refuses each example (R5.2)', () => {
  for (const [value, reason] of R52) {
    const r = scope(value);
    if (reason === null) {
      assert.equal(r.status, 0, value);
      assert.deepEqual(r.out, { problems: [] }, value);
    } else {
      assert.equal(r.status, 1, value);
      assert.deepEqual(r.out, { problems: [reason] }, value);
    }
  }
});

const CORPUS = [
  'feat/PROJ-123-reset', 'feat/proj-123-reset', 'feat/PROJ-123', 'feat/PROJ-123/reset', 'origin/feat/PROJ-123-reset',
  'change/2026-01-01-sample', 'feat/PROJ 123', 'feat//x', '/feat', 'feat/', 'feat/../main', 'feat/@{1}', '@', '@x',
  'a.lock/b', 'a/b.lock', '.a', 'a/.b', 'a.', 'a./b', 'a//b', 'a~b', 'a^b', 'a:b', 'a?b', 'a*b', 'a[b', 'a\\b',
  'café/日本', 'a\tb', 'a\x7fb', 'feat/x..y', 'a@b', '-D', 'change/pending-1730000000', 'x', 'feat/PROJ-123.', 'HEAD',
];

test('branch-name agrees with git check-ref-format', () => {
  assert.ok(CORPUS.length >= 30);
  for (const name of CORPUS) {
    const git = spawnSync('git', ['check-ref-format', '--branch', name], { encoding: 'utf8' });
    const refused = branchName(name).status === 1;
    if (git.status !== 0 || name === '@') {
      assert.ok(refused, `git refuses ${JSON.stringify(name)} but we accept it`);
    } else if (name.startsWith('-') || name.startsWith('change/pending-')) {
      assert.ok(refused, `${JSON.stringify(name)} is reserved`);
    } else {
      assert.ok(!refused, `git accepts ${JSON.stringify(name)} but we refuse it`);
    }
  }
});

test('naming renders the example names', () => {
  const setting = { branch: '{type}/{scope}-{slug}', pullRequestTitle: TITLE };
  const base = ['--type', 'feat', '--scope', 'PROJ-123', '--change', '2026-11-02-password-reset'];
  const r = naming(setting, [...base, '--title', 'Customer can reset password']);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.out.problems, []);
  assert.deepEqual(r.out.names, {
    branch: 'feat/PROJ-123-password-reset',
    pullRequestTitle: 'feat(PROJ-123): Customer can reset password',
  });
  assert.equal(naming(setting, base).out.names.pullRequestTitle, null);
});

test('naming reports undeclared', () => {
  const r = naming(undefined);
  assert.equal(r.status, 0, r.stderr);
  assert.equal(r.out.declared, false);
  assert.deepEqual(r.out.problems, []);
  assert.equal(r.out.names, undefined);
});

const PER_CHANGE = 'the branch pattern needs {slug}, {change} or {scope}, so that each change gets its own branch';

test('naming refuses a branch pattern with no part that differs per change', () => {
  for (const pattern of ['release', '{type}', '{type}/fixed']) {
    const r = naming({ branch: pattern, pullRequestTitle: TITLE });
    assert.equal(r.status, 1, pattern);
    assert.ok(r.out.problems.includes(PER_CHANGE), `${pattern}: ${JSON.stringify(r.out.problems)}`);
  }
  for (const pattern of ['{type}/{slug}', '{type}/{change}', '{type}/{scope}']) {
    const r = naming({ branch: pattern, pullRequestTitle: TITLE });
    assert.equal(r.status, 0, pattern);
    assert.deepEqual(r.out.problems, [], pattern);
  }
});

test('naming reports a pattern naming a part that only exists on Object.prototype as unknown, and nothing else', () => {
  const r = naming({ branch: '{type}/{constructor}-{slug}', pullRequestTitle: 'x' });
  assert.equal(r.status, 1);
  assert.deepEqual(r.out.problems, ['"constructor" is not a known part of a name']);
});
