import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { bareEnv, dotnet, program } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

function scratch() {
  return tempDir('hoshin-format-');
}

function run(args, { cwd, env } = {}) {
  return spawnSync(dotnet, [program, ...args], {
    cwd: cwd ?? scratch(),
    env: env ?? bareEnv,
    encoding: 'utf8',
  });
}

const FRONT_MATTER_ONLY = '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n';
const MINIMAL_SPEC =
  FRONT_MATTER_ONLY + '\n```gherkin\nFeature: Demo\n  @R1 @from:D1\n  Rule: One\n    @R1.1\n    Scenario: First\n      Given a thing\n```\n';

test('check runs standalone with dotnet hoshin.dll', () => {
  const dir = scratch();
  const spec = join(dir, 'spec.md');
  writeFileSync(spec, MINIMAL_SPEC);
  const result = run(['check', spec], { cwd: dir, env: bareEnv });
  assert.equal(result.error, undefined);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, '');
});

test('exit 2 on a file that does not exist', () => {
  const dir = scratch();
  const result = run(['check', join(dir, 'spec.md')], { cwd: dir });
  assert.equal(result.status, 2);
  assert.notEqual(result.stderr, '');
});

test('exit 2 on a file whose kind is unknown', () => {
  const dir = scratch();
  const file = join(dir, 'notes.md');
  writeFileSync(file, FRONT_MATTER_ONLY);
  const result = run(['check', file], { cwd: dir });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /notes\.md/);
});

test('exit 2 on a layout newer than the program knows', () => {
  const dir = scratch();
  const file = join(dir, 'spec.md');
  writeFileSync(file, '```jsonc\n{ "layout": 3 }\n```\n');
  const result = run(['check', file], { cwd: dir });
  assert.equal(result.status, 2);
});

test('exit 2 on YAML front matter or layout 1', () => {
  const dir = scratch();
  const yamlFile = join(dir, 'spec.md');
  writeFileSync(yamlFile, '---\nlayout: 2\n---\n');
  assert.equal(run(['check', yamlFile], { cwd: dir }).status, 2);
  writeFileSync(yamlFile, '```jsonc\n{ "layout": 1 }\n```\n');
  assert.equal(run(['check', yamlFile], { cwd: dir }).status, 2);
});

test('read --json returns the expected envelope for each document kind', () => {
  const dir = scratch();
  mkdirSync(join(dir, 'build'));
  const files = ['spec.md', 'plan.md', 'build/kata.md', 'build/review.md'];
  const kinds = ['spec', 'plan', 'build-log', 'review'];
  files.forEach((name, i) => {
    const file = join(dir, name);
    writeFileSync(file, kinds[i] === 'spec' ? MINIMAL_SPEC : FRONT_MATTER_ONLY);
    const result = run(['read', '--json', file], { cwd: dir });
    const clean = kinds[i] !== 'review';
    assert.equal(result.status, clean ? 0 : 1, result.stderr);
    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.kind, kinds[i]);
    assert.equal(parsed.layout, 2);
    assert.deepEqual(
      parsed.problems,
      clean ? [] : [{ file, line: 1, message: 'review document has no review section' }],
    );
  });
});
