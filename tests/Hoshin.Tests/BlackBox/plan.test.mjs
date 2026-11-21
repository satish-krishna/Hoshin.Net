import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { bareEnv, dotnet, program, testsRoot } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const part2Plan = join(testsRoot, 'Fixtures', 'part-2', 'plan.md');

function run(args, cwd) {
  return spawnSync(dotnet, [program, ...args], {
    cwd: cwd ?? tmpdir(),
    env: bareEnv,
    encoding: 'utf8',
  });
}

function planFile(body, repos) {
  const dir = tempDir('hoshin-plan-');
  if (repos) writeFileSync(join(dir, '.hoshin.json'), JSON.stringify({ version: 1, systems: { demo: { repos } } }));
  const file = join(dir, 'plan.md');
  writeFileSync(file, body);
  return file;
}

function read(file) {
  const r = run(['read', '--json', file]);
  assert.ok(r.status === 0 || r.status === 1, r.stderr);
  return JSON.parse(r.stdout);
}

const FM2 = '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n';
const FENCE = '```';

function task2(id, { fields, tests = true, name = 'Do the thing' } = {}) {
  const block = fields === undefined ? JSON.stringify({ id, repo: 'app', depends_on: [] }, null, 2) : fields;
  return [
    `### ${id}: ${name}`,
    '',
    ...(block === null ? [] : [`${FENCE}jsonc`, block, FENCE, '']),
    '#### Change',
    '',
    'Change it.',
    '',
    ...(tests ? ['#### Tests', '', 'Test it.', ''] : []),
    '#### Done when',
    '',
    'It works.',
    '',
  ].join('\n');
}

const lineOf = (text, needle) => text.split('\n').findIndex((l) => l.includes(needle)) + 1;

test('check is deterministic on a plan with two problems', () => {
  const body = `${FM2}\n# Plan\n\n## Tasks\n\n${task2('T1', { fields: null })}${task2('T2', { tests: false })}`;
  const file = planFile(body);
  const first = run(['check', file]);
  const second = run(['check', file]);
  assert.equal(first.status, 1, first.stdout + first.stderr);
  assert.equal(first.stdout, second.stdout);
  const lines = first.stdout.replace(/\r\n/g, '\n').trimEnd().split('\n');
  assert.equal(lines.length, 2, first.stdout);
  assert.ok(lines[0].endsWith(`plan.md:${lineOf(body, '### T1')}: task T1 has no fields block`), lines[0]);
  assert.match(lines[1], /plan\.md:\d+: task T2 has no Tests section$/);
});

test('every plan layout problem is reported with file and line', () => {
  const heads = `${FM2}\n# Plan\n\n## Tasks\n\n`;
  const mismatchBody = heads + task2('T7', { fields: '{\n  "id": "T8",\n  "repo": "app"\n}' });
  const mismatch = planFile(mismatchBody);
  let r = run(['check', mismatch]);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.stdout.replace(/\r\n/g, '\n'), `${mismatch}:${lineOf(mismatchBody, '"id": "T8"')}: heading says T7, fields say T8\n`);

  const noTestsBody = heads + task2('T7', { tests: false });
  const noTests = planFile(noTestsBody);
  r = run(['check', noTests]);
  assert.equal(r.stdout.replace(/\r\n/g, '\n'), `${noTests}:${lineOf(noTestsBody, '### T7')}: task T7 has no Tests section\n`);

  const noFieldsBody = `${heads}### T7: Bare\n\n#### Tests\n\nTest it.\n`;
  const noFields = planFile(noFieldsBody);
  r = run(['check', noFields]);
  assert.equal(r.stdout.replace(/\r\n/g, '\n'), `${noFields}:${lineOf(noFieldsBody, '### T7')}: task T7 has no fields block\n`);

  const strayBody = heads + task2('T7', { fields: '{\n  "id": "T7",\n  "repo": "billing"\n}' });
  const stray = planFile(strayBody, ['app', 'web']);
  r = run(['check', stray]);
  assert.equal(r.stdout.replace(/\r\n/g, '\n'), `${stray}:${lineOf(strayBody, '"repo": "billing"')}: task T7 names repository billing, not in system demo\n`);

  const listed = planFile(strayBody, ['app', 'billing']);
  assert.equal(run(['check', listed]).stdout, '');
});

test('layout 2 reads fields, sections and shared decisions', () => {
  const body = [
    FM2,
    '# Plan',
    '',
    '## Shared decisions',
    '',
    `${FENCE}jsonc`,
    JSON.stringify([{ question: 'How is a problem reported', owner: 'T1', function: 'formatProblem' }], null, 2),
    FENCE,
    '',
    '## Tasks',
    '',
    task2('T1', { fields: JSON.stringify({ id: 'T1', repo: 'app', depends_on: [], uses: [], files: ['a.ts', 'b.ts'], covers: ['R1.1'], supports: ['R2.1'] }, null, 2) }),
    task2('T2', { fields: JSON.stringify({ id: 'T2', repo: 'app', depends_on: ['T1'], uses: ['how a problem is reported'] }, null, 2) }),
  ].join('\n');
  const file = planFile(body);
  const result = read(file);
  assert.equal(result.kind, 'plan');
  assert.equal(result.layout, 2);
  assert.deepEqual(result.problems, []);
  const [t1, t2] = result.document.tasks;
  assert.deepEqual(
    [t1.id, t1.name, t1.repo, t1.depends_on, t1.files, t1.covers, t1.supports],
    ['T1', 'Do the thing', 'app', [], ['a.ts', 'b.ts'], ['R1.1'], ['R2.1']],
  );
  assert.deepEqual(t2.depends_on, ['T1']);
  assert.deepEqual(t2.uses, ['how a problem is reported']);
  assert.deepEqual(t1.sections.map((s) => s.title), ['Change', 'Tests', 'Done when']);
  assert.equal(t1.sections[1].body, 'Test it.');
  assert.equal(t1.line, lineOf(body, '### T1: Do the thing'));
  assert.match(t1.text, /^### T1: Do the thing/);
  assert.deepEqual(result.document.shared_decisions, [
    { question: 'How is a problem reported', owner: 'T1', function: 'formatProblem' },
  ]);
});

test('malformed shared decisions are reported', () => {
  const body = (jsonc) => `${FM2}\n# Plan\n\n## Shared decisions\n\n${FENCE}jsonc\n${jsonc}\n${FENCE}\n\n## Tasks\n\n${task2('T1')}`;
  const b1 = body('[{"question": "Why", "owner": "T1"}]');
  let file = planFile(b1);
  assert.equal(run(['check', file]).stdout.replace(/\r\n/g, '\n'), `${file}:${lineOf(b1, '[{"question"') - 1}: shared decision 1 lacks function\n`);
  const b2 = body('{"question": "Why"}');
  file = planFile(b2);
  assert.equal(run(['check', file]).stdout.replace(/\r\n/g, '\n'), `${file}:${lineOf(b2, '{"question"') - 1}: shared decisions are not a list\n`);
});

test('a layout 2 plan without a layout version is reported', () => {
  const file = planFile(`\`\`\`jsonc\n{ "system": "demo" }\n\`\`\`\n\n## Tasks\n\n${task2('T1')}`);
  const r = run(['check', file]);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.stdout.replace(/\r\n/g, '\n'), `${file}:1: no layout version\n`);
});

test('a layout 1 plan is refused with exit 2', () => {
  const file = planFile('```jsonc\n{ "layout": 1, "system": "demo" }\n```\n');
  const r = run(['check', file]);
  assert.equal(r.status, 2);
  assert.match(r.stderr, /layout 1/);
});

test('the converted Layout 2 part-2 plan yields its 30 tasks with their dependencies', () => {
  const r = run(['check', part2Plan]);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  const result = read(part2Plan);
  assert.equal(result.layout, 2);
  const tasks = result.document.tasks;
  assert.equal(tasks.length, 30);
  assert.deepEqual(tasks.slice(0, 3).map((t) => t.id), ['T31', 'T18', 'T32']);
  const byId = Object.fromEntries(tasks.map((t) => [t.id, t]));
  assert.deepEqual(byId.T31.depends_on, []);
  assert.deepEqual(byId.T33.depends_on, ['T18', 'T32']);
  assert.deepEqual(byId.T38.depends_on, ['T33', 'T35', 'T20', 'T22', 'T8', 'T37']);
  assert.deepEqual(byId.T50.depends_on, ['T28']);
  assert.equal(byId.T32.repo, 'kata');
});

test('a layout 2 task reads its test and compile commands, and null when absent', () => {
  const fields = JSON.stringify({ id: 'T1', repo: 'app', test: ['cargo', 'test', 'greeting'], compile: ['cargo', 'test', 'greeting', '--no-run'] }, null, 2);
  const result = read(planFile(`${FM2}\n## Tasks\n${task2('T1', { fields })}${task2('T2', { fields: '{"id":"T2","repo":"app"}' })}`));
  assert.deepEqual(result.problems, []);
  const [t1, t2] = result.document.tasks;
  assert.deepEqual(t1.test, ['cargo', 'test', 'greeting']);
  assert.deepEqual(t1.compile, ['cargo', 'test', 'greeting', '--no-run']);
  assert.equal(t2.test, null);
  assert.equal(t2.compile, null);
});

test('a test that is a string or an empty list is reported', () => {
  let file = planFile(`${FM2}\n## Tasks\n${task2('T1', { fields: '{"id":"T1","repo":"app","test":"cargo test"}' })}`);
  assert.match(run(['check', file]).stdout, /task T1 fields field test must be array/);
  file = planFile(`${FM2}\n## Tasks\n${task2('T1', { fields: '{"id":"T1","repo":"app","test":[]}' })}`);
  assert.match(run(['check', file]).stdout, /task T1 fields field test must NOT have fewer than 1 items/);
});

test('a test or compile that is not a non-empty list of non-empty strings reads as null', () => {
  const fields = '{"id":"T1","repo":"app","test":[],"compile":["cargo",3]}';
  const [t1] = read(planFile(`${FM2}\n## Tasks\n${task2('T1', { fields })}`)).document.tasks;
  assert.equal(t1.test, null);
  assert.equal(t1.compile, null);
});
