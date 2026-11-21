import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { bareEnv, dotnet, program } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

function run(rel, text, ...args) {
  const dir = tempDir('hoshin-fences-');
  mkdirSync(dirname(join(dir, rel)), { recursive: true });
  writeFileSync(join(dir, rel), text);
  const r = spawnSync(dotnet, [program, ...args, rel], { cwd: dir, encoding: 'utf8', env: bareEnv });
  assert.equal(r.error, undefined);
  return r;
}
const check = (rel, text) => {
  const r = run(rel, text, 'check');
  return { out: r.stdout.replace(/\r\n/g, '\n').split('\n').filter((l) => l !== ''), status: r.status };
};
const read = (rel, text, pick) => pick(JSON.parse(run(rel, text, 'read', '--json').stdout).document);
const lineOf = (text, needle) => text.split('\n').findIndex((l) => l === needle) + 1;
const blockUnder = (text, heading) => lineOf(text, heading) + 2;

const PLAN_HEAD = '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n# Plan: Example\n\n## Tasks\n\n';
const planTask = (n, change = 'Change it.') =>
  `### T${n}: Task ${n}\n\n\`\`\`jsonc\n{\n  "id": "T${n}",\n  "repo": "app"\n}\n\`\`\`\n\n#### Change\n\n${change}\n\n#### Tests\n\nTest it first.\n\n#### Done when\n\nIt works.\n\n`;
const LOG_HEAD = '```jsonc\n{ "layout": 2 }\n```\n\n# Build log\n\n';
const logEntry = (n, prose = '') => `### T${n}\n\n\`\`\`jsonc\n{\n  "task": "T${n}",\n  "commits": ["abc123${n}"]\n}\n\`\`\`\n\n${prose}`;
const REVIEW_HEAD =
  '```jsonc\n{ "layout": 2 }\n```\n\n# Review findings\n\n## Review 1\n\n```jsonc\n{\n  "review": 1,\n  "reviewed_at": "2026-10-22",\n  "commits": { "kata": "840fa0c" }\n}\n```\n\n';
const finding = (n, prose = 'Text.\n') => `### F${n}\n\n\`\`\`jsonc\n{\n  "id": "F${n}",\n  "severity": "low",\n  "file": "a.ts",\n  "line": 1\n}\n\`\`\`\n\n${prose}\n`;
const taskIds = (d) => d.tasks.map((t) => t.id);
const entryIds = (d) => d.entries.map((e) => e.task);
const findingIds = (d) => d.reviews.flatMap((r) => r.findings.map((f) => f.id));

const QUOTE = '````md\n### T9: Example\n\n```jsonc\n{\n  "id": "<task id>",\n  "repo": "app"\n}\n```\n````\n';
const LOG_QUOTE = '````md\n### T9\n\n```jsonc\n{\n  "task": "<task id>",\n  "commits": ["abc1239"]\n}\n```\n````\n';
const REVIEW_QUOTE = '````md\n### F9\n\n```jsonc\n{\n  "id": "<finding id>",\n  "severity": "low",\n  "file": "a.ts",\n  "line": 1\n}\n```\n````\n';

test('a plan with a four-backtick block quoting a record passes and reads both tasks', () => {
  const text = PLAN_HEAD + planTask(1, 'Quote this:\n\n' + QUOTE) + planTask(2);
  assert.deepEqual(check('plan.md', text), { out: [], status: 0 });
  assert.deepEqual(read('plan.md', text, taskIds), ['T1', 'T2']);
});

test('a build log with a four-backtick block quoting a record passes and reads both entries', () => {
  const text = LOG_HEAD + logEntry(1, 'Quote this:\n\n' + LOG_QUOTE + '\n') + logEntry(2);
  assert.deepEqual(check('build/app.md', text), { out: [], status: 0 });
  assert.deepEqual(read('build/app.md', text, entryIds), ['T1', 'T2']);
});

test('a review with a four-backtick block quoting a record passes and reads both findings', () => {
  const text = REVIEW_HEAD + finding(1, 'Quote this:\n\n' + REVIEW_QUOTE) + finding(2);
  assert.deepEqual(check('build/review.md', text), { out: [], status: 0 });
  assert.deepEqual(read('build/review.md', text, findingIds), ['F1', 'F2']);
});

test('a tilde block inside a longer tilde block is content', () => {
  const text = PLAN_HEAD + planTask(1, 'Quote this:\n\n~~~~md\n### T9: Example\n\n~~~jsonc\n{ "id": "<task id>" }\n~~~\n~~~~\n') + planTask(2);
  assert.deepEqual(check('plan.md', text), { out: [], status: 0 });
  assert.deepEqual(read('plan.md', text, taskIds), ['T1', 'T2']);
});

for (const fence of ['```', '~~~']) {
  test(`a plan with an unclosed ${fence} fence in a task's prose reports its opening line`, () => {
    const text = PLAN_HEAD + planTask(1) + planTask(2, `Sketch:\n\n${fence}ts\nconst a = 1;\n`) + planTask(3);
    const at = lineOf(text, fence + 'ts');
    const swallowed = fence === '~~~' ? [`plan.md:${lineOf(text, '### T2: Task 2')}: task T2 has no Tests section`] : [];
    const hidden =
      fence === '```'
        ? [`plan.md:${lineOf(text, '  "id": "T3",')}: "id": "T3" is not part of any record the reader found`]
        : [`plan.md:${at}: fenced block opened on line ${at} is never closed`];
    assert.deepEqual(check('plan.md', text), { out: [...swallowed, ...hidden], status: 1 });
  });

  test(`a build log with an unclosed ${fence} fence in an entry's prose reports its opening line`, () => {
    const text = LOG_HEAD + logEntry(1) + logEntry(2, `Sketch:\n\n${fence}ts\nconst a = 1;\n\n`) + logEntry(3);
    const at = lineOf(text, fence + 'ts');
    const hidden =
      fence === '```'
        ? `build/app.md:${lineOf(text, '  "task": "T3",')}: "task": "T3" is not part of any record the reader found`
        : `build/app.md:${at}: fenced block opened on line ${at} is never closed`;
    assert.deepEqual(check('build/app.md', text), { out: [hidden], status: 1 });
  });

  test(`a review with an unclosed ${fence} fence in a finding reports its opening line`, () => {
    const text =
      REVIEW_HEAD +
      finding(1, `Sketch:\n\n${fence}ts\nconst a = 1;\n`) +
      finding(2) +
      '## Review 2\n\n```jsonc\n{\n  "review": 2,\n  "reviewed_at": "2026-10-23",\n  "commits": { "kata": "aaa1111" }\n}\n```\n';
    const at = lineOf(text, fence + 'ts');
    const hidden =
      fence === '```'
        ? `build/review.md:${lineOf(text, '  "id": "F2",')}: "id": "F2" is not part of any record the reader found`
        : `build/review.md:${at}: fenced block opened on line ${at} is never closed`;
    assert.deepEqual(check('build/review.md', text), { out: [hidden], status: 1 });
  });
}

const BROKEN = [
  ['an unclosed array', '{\n  "id": "T2",\n  "repo": "app",\n  "depends_on": ["T1"\n}\n'],
  ['a missing closing brace', '{\n  "id": "T2",\n  "repo": "app"\n'],
  ['a missing colon', '{\n  "id": "T2",\n  "repo" "app"\n}\n'],
];
for (const [name, body] of BROKEN) {
  test(`a plan task under a near-miss heading is reported when its fields block has ${name}`, () => {
    const heading = '### T2a: Second task';
    const text = PLAN_HEAD + planTask(1) + heading + '\n\n```jsonc\n' + body + '```\n\n#### Change\n\nChange it.\n';
    assert.deepEqual(check('plan.md', text), { out: [`plan.md:${blockUnder(text, heading)}: jsonc block is not part of any record`], status: 1 });
  });
}

test('a build log entry under a near-miss heading is reported when its fields block does not parse', () => {
  const text = LOG_HEAD + logEntry(1) + '### T2a\n\n```jsonc\n{\n  "task": "T2",\n  "commits": ["abc1234"\n}\n```\n';
  assert.deepEqual(check('build/app.md', text), { out: [`build/app.md:${blockUnder(text, '### T2a')}: jsonc block is not part of any record`], status: 1 });
});

test('a review finding under a near-miss heading is reported when its fields block does not parse', () => {
  const text = REVIEW_HEAD + '### F2a\n\n```jsonc\n{\n  "id": "F2",\n  "severity": ["low"\n}\n```\n';
  assert.deepEqual(check('build/review.md', text), { out: [`build/review.md:${blockUnder(text, '### F2a')}: jsonc block is not part of any record`], status: 1 });
});

for (const opener of ['```json', '```JSONC', '``` jsonc', '```Jsonc', '~~~jsonc']) {
  const close = opener.slice(0, 3);
  test(`a fields block opened as "${opener}" is read under a task heading`, () => {
    const text =
      PLAN_HEAD +
      planTask(1) +
      `### T2: Second task\n\n${opener}\n{\n  "id": "T2",\n  "repo": "app"\n}\n${close}\n\n#### Change\n\nChange it.\n\n#### Tests\n\nTest it first.\n\n#### Done when\n\nIt works.\n`;
    assert.deepEqual(check('plan.md', text), { out: [], status: 0 });
    assert.deepEqual(read('plan.md', text, taskIds), ['T1', 'T2']);
  });

  test(`a fields block opened as "${opener}" under a near-miss heading is reported`, () => {
    const text = PLAN_HEAD + planTask(1) + `### T2a: Second task\n\n${opener}\n{\n  "id": "T2",\n  "repo": "app"\n}\n${close}\n\n#### Change\n\nChange it.\n`;
    assert.deepEqual(check('plan.md', text), { out: [`plan.md:${lineOf(text, opener)}: jsonc block is not part of any record`], status: 1 });
  });
}

test('a jsonc example with no record key that does not parse stays ordinary content', () => {
  const text = PLAN_HEAD + planTask(1, 'Config:\n\n```jsonc\n{ "ci": [true }\n```\n') + planTask(2);
  assert.deepEqual(check('plan.md', text), { out: [], status: 0 });
});
