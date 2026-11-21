import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { bareEnv, dotnet, program } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const FENCE = '```';

function stage(rel, body, repos) {
  const dir = tempDir('hoshin-problems-');
  if (repos) writeFileSync(join(dir, '.hoshin.json'), JSON.stringify({ version: 1, systems: { kata: { repos } } }));
  mkdirSync(dirname(join(dir, rel)), { recursive: true });
  writeFileSync(join(dir, rel), body);
  return { dir, rel };
}

function check(rel, body, repos) {
  const s = stage(rel, body, repos);
  const r = spawnSync(dotnet, [program, 'check', s.rel], {
    cwd: s.dir,
    env: bareEnv,
    encoding: 'utf8',
  });
  return { status: r.status, out: r.stdout.replace(/\r\n/g, '\n'), err: r.stderr };
}

function lineOf(body, needle) {
  const i = body.split('\n').findIndex((l) => l.includes(needle));
  assert.ok(i >= 0, `no line contains ${needle}`);
  return i + 1;
}

const spec = (rules) => `${FENCE}jsonc\n{ "layout": 2 }\n${FENCE}\n\n# Spec\n\n${FENCE}gherkin\nFeature: F\n\n${rules}\n${FENCE}\n`;

const task = (id, { fields, tests = true } = {}) =>
  [
    `### ${id}: Do it`,
    '',
    ...(fields === null ? [] : [`${FENCE}jsonc`, fields ?? `{\n  "id": "${id}",\n  "repo": "app"\n}`, FENCE, '']),
    '#### Change',
    '',
    'Change.',
    '',
    ...(tests ? ['#### Tests', '', 'Test.', ''] : []),
    '#### Done when',
    '',
    'Done.',
    '',
  ].join('\n');
const plan = (tasks) => `${FENCE}jsonc\n{\n  "layout": 2,\n  "system": "kata"\n}\n${FENCE}\n\n# Plan\n\n## Tasks\n\n${tasks}`;

const buildLog = (entry) => `${FENCE}jsonc\n{ "layout": 2 }\n${FENCE}\n\n# Build log: app\n\n${entry}`;
const review = (sections) => `${FENCE}jsonc\n{ "layout": 2 }\n${FENCE}\n\n# Review findings\n\n${sections}`;
const reviewHeader = (n) =>
  `## Review ${n}\n\n${FENCE}jsonc\n{\n  "review": ${n},\n  "reviewed_at": "2026-10-22",\n  "commits": { "app": "abc1234" }\n}\n${FENCE}\n`;

const cases = [
  {
    name: 'spec: a requirement has no ID',
    rel: 'spec.md',
    body: spec('  Rule: Nameless\n\n    @R1.1\n    Scenario: s\n      Given a'),
    at: 'Rule: Nameless',
    message: 'requirement has no ID',
  },
  {
    name: "spec: a scenario's ID belongs to a different requirement",
    rel: 'spec.md',
    body: spec('  @R4 @from:x\n  Rule: Four\n\n    @R3.2\n    Scenario: stray\n      Given a'),
    at: 'Scenario: stray',
    message: 'scenario R3.2 sits under requirement R4',
  },
  {
    name: 'spec: a requirement has no scenario',
    rel: 'spec.md',
    body: spec('  @R5 @from:x\n  Rule: Empty'),
    at: 'Rule: Empty',
    message: 'requirement R5 has no scenario',
  },
  {
    name: 'spec: an outline uses a value its examples do not list',
    rel: 'spec.md',
    body: spec(
      '  @R2 @from:x\n  Rule: Two\n\n    @R2.2\n    Scenario Outline: pays\n      Given <amount>\n\n      Examples:\n        | other |\n        | 1     |',
    ),
    at: 'Scenario Outline: pays',
    message: 'scenario R2.2 uses <amount> but its examples lack it',
  },
  {
    name: 'plan: a task has no fields block',
    rel: 'plan.md',
    body: plan(task('T7', { fields: null })),
    at: '### T7',
    message: 'task T7 has no fields block',
  },
  {
    name: "plan: a task's heading and fields name different tasks",
    rel: 'plan.md',
    body: plan(task('T7', { fields: '{\n  "id": "T8",\n  "repo": "app"\n}' })),
    at: '"id": "T8"',
    message: 'heading says T7, fields say T8',
  },
  {
    name: 'plan: a task has no Tests section',
    rel: 'plan.md',
    body: plan(task('T7', { tests: false })),
    at: '### T7',
    message: 'task T7 has no Tests section',
  },
  {
    name: 'plan: a task names a repository the system does not list',
    rel: 'plan.md',
    body: plan(task('T7', { fields: '{\n  "id": "T7",\n  "repo": "billing"\n}' })),
    repos: ['app', 'web'],
    at: '"repo": "billing"',
    message: 'task T7 names repository billing, not in system kata',
  },
  {
    name: 'build log: an entry has no commits',
    rel: join('build', 'app.md'),
    body: buildLog(`### T7\n\n${FENCE}jsonc\n{\n  "task": "T7",\n  "commits": []\n}\n${FENCE}\n`),
    at: '### T7',
    message: 'entry T7 has no commits',
  },
  {
    name: 'build log: an entry omits commits',
    rel: join('build', 'app.md'),
    body: buildLog(`### T7\n\n${FENCE}jsonc\n{\n  "task": "T7"\n}\n${FENCE}\n`),
    at: '### T7',
    message: 'entry T7 has no commits',
  },
  {
    name: 'review findings: a finding has no severity',
    rel: join('build', 'review.md'),
    body: review(`${reviewHeader(1)}\n### F3\n\n${FENCE}jsonc\n{\n  "id": "F3",\n  "file": "a.ts",\n  "line": 4\n}\n${FENCE}\n\nIt breaks.\n`),
    at: '### F3',
    message: 'finding F3 has no severity',
  },
  {
    name: 'any: the layout version is missing (spec)',
    rel: 'spec.md',
    body: '# Spec\n\n```gherkin\nFeature: F\n\n  @R1 @from:x\n  Rule: A\n\n    @R1.1\n    Scenario: s\n      Given a\n```\n',
    line: 1,
    message: 'no layout version',
  },
  {
    name: 'any: the layout version is missing (plan)',
    rel: 'plan.md',
    body: `# Plan\n\n## Tasks\n\n${task('T1')}`,
    line: 1,
    message: 'no layout version',
  },
  {
    name: 'any: the layout version is missing (build log)',
    rel: join('build', 'app.md'),
    body: `# Build log: app\n\n### T1\n\n${FENCE}jsonc\n{\n  "task": "T1",\n  "commits": ["abc1234"]\n}\n${FENCE}\n`,
    line: 1,
    message: 'no layout version',
  },
  {
    name: 'any: the layout version is missing (review)',
    rel: join('build', 'review.md'),
    body: `# Review findings\n\n${reviewHeader(1)}`,
    line: 1,
    message: 'no layout version',
  },
];

test('every layout problem row is reported with file and line', () => {
  for (const c of cases) {
    const line = c.line ?? lineOf(c.body, c.at);
    const r = check(c.rel, c.body, c.repos);
    assert.equal(r.status, 1, `${c.name}: ${r.out}${r.err}`);
    const lines = r.out.trimEnd().split('\n');
    assert.deepEqual(
      lines.map((l) => l.replace(/^.*?([^\\/:]+\.md):/, '$1:')),
      [`${c.rel.split(/[\\/]/).pop()}:${line}: ${c.message}`],
      c.name,
    );
  }
});

test('a build log reports a task recorded twice, on the second entry', () => {
  const entry = (n) => `### T1\n\n${FENCE}jsonc\n{\n  "task": "T1",\n  "commits": ["abc123${n}"]\n}\n${FENCE}\n`;
  const body = buildLog(`${entry(1)}\n${entry(2)}`);
  const r = check(join('build', 'app.md'), body);
  assert.equal(r.status, 1, r.out + r.err);
  const second = body.split('\n').lastIndexOf('### T1') + 1;
  assert.match(r.out.trimEnd(), new RegExp(`app\\.md:${second}: entry T1 is recorded twice, first on line \\d+$`));
});

test('reviews must be numbered 1, 2, 3 in order', () => {
  const body = review(`${reviewHeader(1)}\n${reviewHeader(3)}`);
  const r = check(join('build', 'review.md'), body);
  assert.equal(r.status, 1, r.out + r.err);
  assert.match(
    r.out.trimEnd(),
    new RegExp(`review\\.md:${lineOf(body, '## Review 3')}: review 3 is out of order, expected review 2$`),
  );
});
