import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { bareEnv, dotnet, program, testsRoot } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const part2Spec = join(testsRoot, 'Fixtures', 'part-2', 'spec.md');

function run(args, cwd) {
  return spawnSync(dotnet, [program, ...args], {
    cwd: cwd ?? tmpdir(),
    env: bareEnv,
    encoding: 'utf8',
  });
}

function specFile(body) {
  const dir = tempDir('hoshin-spec-');
  const file = join(dir, 'spec.md');
  writeFileSync(file, body);
  return file;
}

function layout2(...gherkin) {
  return '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n# Spec\n\n```gherkin\n' + gherkin.join('\n') + '\n```\n';
}

function read(file) {
  const r = run(['read', '--json', file]);
  assert.ok(r.status === 0 || r.status === 1, r.stderr);
  return JSON.parse(r.stdout);
}

const lineOf = (text, needle) => text.split('\n').findIndex((l) => l.includes(needle)) + 1;

test('a scenario ID used twice is reported', () => {
  const body = layout2(
    'Feature: Demo',
    '',
    '  @R2 @from:D1',
    '  Rule: Things add up',
    '',
    '    @R2.1',
    '    Scenario: First',
    '      Given a number',
    '',
    '    @R2.1',
    '    Scenario: Second',
    '      Given another number',
  );
  const file = specFile(body);
  const r = run(['check', file]);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.stdout.replace(/\r\n/g, '\n'), `${file}:${lineOf(body, 'Scenario: Second')}: scenario ID R2.1 is used twice\n`);
});

test('the converted Layout 2 part-2 spec reads its 20 requirements and 77 scenarios', () => {
  const r = run(['check', part2Spec]);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.equal(r.stdout, '');
  const result = read(part2Spec);
  assert.equal(result.kind, 'spec');
  assert.equal(result.layout, 2);
  assert.deepEqual(result.problems, []);
  const reqs = result.document.requirements;
  assert.equal(reqs.length, 20);
  assert.equal(reqs.reduce((n, q) => n + q.scenarios.length, 0), 77);
  assert.equal(reqs[0].id, 'R1');
  assert.deepEqual(reqs[0].from, ['Proposed-outcome-1', 'D13']);
});

test('layout 1 specs are refused with exit 2', () => {
  const file = specFile('```jsonc\n{ "layout": 1, "system": "demo" }\n```\n');
  const r = run(['check', file]);
  assert.equal(r.status, 2);
  assert.match(r.stderr, /layout 1/);
});

test('layout 2 reads IDs and sources from tags', () => {
  const body = layout2(
    'Feature: Demo',
    '  @R7 @from:outcome-4 @from:D2',
    '  Rule: The engineer sees what changed',
    '    @R7.1',
    '    Scenario: What changed',
    '      Given a build',
    '      When it ends',
  );
  const result = read(specFile(body));
  assert.equal(result.layout, 2);
  assert.deepEqual(result.problems, []);
  const [q] = result.document.requirements;
  assert.deepEqual([q.id, q.title, q.from, q.line], ['R7', 'The engineer sees what changed', ['outcome-4', 'D2'], lineOf(body, 'Rule: The engineer')]);
  assert.deepEqual(q.scenarios[0], {
    id: 'R7.1',
    title: 'What changed',
    outline: false,
    steps: ['Given a build', 'When it ends'],
    examples: null,
    line: lineOf(body, 'Scenario: What changed'),
  });
});

const rows = [
  {
    name: 'a requirement has no ID',
    gherkin: ['Feature: Demo', '  @from:D1', '  Rule: Nameless', '    @R1.1', '    Scenario: One', '      Given x'],
    anchor: 'Rule: Nameless',
    message: 'requirement has no ID',
  },
  {
    name: "a scenario's ID belongs to a different requirement",
    gherkin: ['Feature: Demo', '  @R4 @from:D1', '  Rule: Four', '    @R3.2', '    Scenario: Stray', '      Given x'],
    anchor: 'Scenario: Stray',
    message: 'scenario R3.2 sits under requirement R4',
  },
  {
    name: 'a requirement has no scenario',
    gherkin: ['Feature: Demo', '  @R5 @from:D1', '  Rule: Empty'],
    anchor: 'Rule: Empty',
    message: 'requirement R5 has no scenario',
  },
  {
    name: 'an outline uses a value its examples do not list',
    gherkin: [
      'Feature: Demo',
      '  @R2 @from:D1',
      '  Rule: Two',
      '    @R2.2',
      '    Scenario Outline: Pays',
      '      Given <amount> and <who>',
      '      Examples:',
      '        | who |',
      '        | me  |',
    ],
    anchor: 'Scenario Outline: Pays',
    message: 'scenario R2.2 uses <amount> but its examples lack it',
  },
];

for (const row of rows) {
  test(`every spec layout problem is reported with file and line: ${row.name}`, () => {
    const body = layout2(...row.gherkin);
    const file = specFile(body);
    const r = run(['check', file]);
    assert.equal(r.status, 1, r.stdout + r.stderr);
    assert.equal(r.stdout.replace(/\r\n/g, '\n'), `${file}:${lineOf(body, row.anchor)}: ${row.message}\n`);
  });
}

test('the layout version is missing', () => {
  const body = '```jsonc\n{ "system": "demo" }\n```\n\n```gherkin\nFeature: Demo\n  @R1 @from:D1\n  Rule: One\n    @R1.1\n    Scenario: A\n      Given x\n```\n';
  const file = specFile(body);
  const r = run(['check', file]);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.stdout.replace(/\r\n/g, '\n'), `${file}:1: no layout version\n`);
});
