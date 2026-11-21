import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { bareEnv, dotnet, program } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

function run(rel, text, ...args) {
  const dir = tempDir('hoshin-raw-');
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
const readDoc = (rel, text) => JSON.parse(run(rel, text, 'read', '--json').stdout).document;
const read = (rel, text, pick) => pick(readDoc(rel, text));
const lineOf = (text, needle) => text.split(/\r\n|\r|\n/).findIndex((l) => l === needle) + 1;
const keyText = (line) => line.trim().replace(/^[\[\{\s]+/, '').replace(/[,\}\]\s]+$/, '');
const unread = (rel, text, key) => `${rel}:${lineOf(text, key)}: ${keyText(key)} is not part of any record the reader found`;

const PLAN_HEAD = '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n# Plan: Example\n\n## Tasks\n\n';
const planTask = (n, change = 'Change it.') =>
  `### T${n}: Task ${n}\n\n\`\`\`jsonc\n{\n  "id": "T${n}",\n  "repo": "app"\n}\n\`\`\`\n\n#### Change\n\n${change}\n\n#### Tests\n\nTest it first.\n\n#### Done when\n\nIt works.\n\n`;
const LOG_HEAD = '```jsonc\n{ "layout": 2 }\n```\n\n# Build log\n\n';
const logEntry = (n, prose = '') => `### T${n}\n\n\`\`\`jsonc\n{\n  "task": "T${n}",\n  "commits": ["abc123${n}"]\n}\n\`\`\`\n\n${prose}`;
const REVIEW_HEAD =
  '```jsonc\n{ "layout": 2 }\n```\n\n# Review findings\n\n## Review 1\n\n```jsonc\n{\n  "review": 1,\n  "reviewed_at": "2026-10-22",\n  "commits": { "kata": "840fa0c" }\n}\n```\n\n';
const finding = (n, prose = 'Text.\n') => `### F${n}\n\n\`\`\`jsonc\n{\n  "id": "F${n}",\n  "severity": "low",\n  "file": "a.ts",\n  "line": 1\n}\n\`\`\`\n\n${prose}\n`;
const REVIEW_2 = '## Review 2\n\n```jsonc\n{\n  "review": 2,\n  "reviewed_at": "2026-10-23",\n  "commits": { "kata": "aaa1111" }\n}\n```\n';
const taskIds = (d) => d.tasks.map((t) => t.id);
const entryIds = (d) => d.entries.map((e) => e.task);
const findingIds = (d) => d.reviews.flatMap((r) => r.findings.map((f) => f.id));

for (const open of ['<!-- TODO: split this', '<pre>', '<script>']) {
  test(`a plan with an unclosed ${open} before the next task reports that task's key line`, () => {
    const text = PLAN_HEAD + planTask(1) + open + '\n\n' + planTask(2);
    assert.deepEqual(check('plan.md', text), { out: [unread('plan.md', text, '  "id": "T2",')], status: 1 });
  });

  test(`a build log with an unclosed ${open} before the next entry reports that entry's key line`, () => {
    const text = LOG_HEAD + logEntry(1, open + '\n\n') + logEntry(2);
    assert.deepEqual(check('build/app.md', text), { out: [unread('build/app.md', text, '  "task": "T2",')], status: 1 });
  });

  test(`a review with an unclosed ${open} in a finding reports the finding and the review after it`, () => {
    const text = REVIEW_HEAD + finding(1, 'Prose.\n' + open + '\n') + finding(2) + REVIEW_2;
    assert.deepEqual(check('build/review.md', text), {
      out: [unread('build/review.md', text, '  "id": "F2",'), unread('build/review.md', text, '  "review": 2,')],
      status: 1,
    });
  });
}

test('a record inside a closed HTML comment is reported', () => {
  const text = PLAN_HEAD + planTask(1) + '<!--\n' + planTask(2) + '-->\n';
  assert.deepEqual(check('plan.md', text), { out: [unread('plan.md', text, '  "id": "T2",')], status: 1 });
});

test('a key line that is indented is reported, whatever the markdown makes of it', () => {
  const text = PLAN_HEAD + planTask(1, 'Note:\n\n  "id": "T8"');
  assert.deepEqual(check('plan.md', text), { out: [unread('plan.md', text, '  "id": "T8"')], status: 1 });
});

const crlf = (text) => text.replace(/\n/g, '\r\n');

test('a CRLF plan with a CR CR LF line before a record still reads both tasks', () => {
  const text = crlf(PLAN_HEAD + planTask(1) + planTask(2)).replace('It works.\r\n', 'It works.\r\r\n');
  assert.deepEqual(check('plan.md', text), { out: [], status: 0 });
  assert.deepEqual(read('plan.md', text, taskIds), ['T1', 'T2']);
});

test('a CRLF build log with a CR CR LF line before a record still reads both entries', () => {
  const text = crlf(LOG_HEAD + logEntry(1) + logEntry(2)).replace('"commits": ["abc1231"]\r\n', '"commits": ["abc1231"]\r\r\n');
  assert.deepEqual(check('build/app.md', text), { out: [], status: 0 });
  assert.deepEqual(read('build/app.md', text, entryIds), ['T1', 'T2']);
});

test('a lone CR in a finding keeps the findings and reviews after it', () => {
  const text = REVIEW_HEAD + finding(1, 'Prose with a\rbare return.\n') + finding(2) + REVIEW_2;
  assert.deepEqual(check('build/review.md', text), { out: [], status: 0 });
  assert.deepEqual(read('build/review.md', text, findingIds), ['F1', 'F2']);
  assert.deepEqual(read('build/review.md', text, (d) => d.reviews.map((r) => r.number)), [1, 2]);
});

test('a problem after CR CR LF is reported at the line an editor shows', () => {
  const plain = PLAN_HEAD + planTask(1) + planTask(2).replace('#### Tests\n\nTest it first.\n\n', '');
  const text = crlf(plain).replace('It works.\r\n', 'It works.\r\r\n');
  assert.deepEqual(check('plan.md', text), { out: [`plan.md:${lineOf(text, '### T2: Task 2')}: task T2 has no Tests section`], status: 1 });
  assert.equal(lineOf(text, '### T2: Task 2'), lineOf(plain, '### T2: Task 2') + 1);
});

test('a three-backtick block quoting an indented fence passes', () => {
  const text = PLAN_HEAD + planTask(1, 'Quote:\n\n```md\n1. item\n\n    ```ts\n    const a = 1;\n    ```\n```\n') + planTask(2);
  assert.deepEqual(check('plan.md', text), { out: [], status: 0 });
  assert.deepEqual(read('plan.md', text, taskIds), ['T1', 'T2']);
});

test('a three-backtick block quoting a blockquoted fence passes', () => {
  const text = PLAN_HEAD + planTask(1, 'Quote:\n\n```md\n> ```ts\n> const a = 1;\n> ```\n```\n') + planTask(2);
  assert.deepEqual(check('plan.md', text), { out: [], status: 0 });
  assert.deepEqual(read('plan.md', text, taskIds), ['T1', 'T2']);
});

test('a stray fence followed by a four-backtick fields block is reported', () => {
  const second = planTask(2).replace('```jsonc', '````jsonc').replace('"repo": "app"\n}\n```', '"repo": "app"\n}\n````');
  const text = PLAN_HEAD + planTask(1, 'Sketch:\n\n```ts\nconst a = 1;\n') + second;
  const r = check('plan.md', text);
  assert.ok(r.out.includes(unread('plan.md', text, '  "id": "T2",')), r.out.join('\n'));
  assert.equal(r.status, 1);
});

const QUOTED = '````md\n### T9: Example\n\n```jsonc\n{\n  "id": "T9",\n  "repo": "app"\n}\n```\n````\n';

test('a quoted record whose key is a real ID is reported', () => {
  const text = PLAN_HEAD + planTask(1, 'Quote:\n\n' + QUOTED) + planTask(2);
  assert.deepEqual(check('plan.md', text), { out: [unread('plan.md', text, '  "id": "T9",')], status: 1 });
});

test('a quoted record whose key is a placeholder passes', () => {
  const text = PLAN_HEAD + planTask(1, 'Quote:\n\n' + QUOTED.replace('"id": "T9"', '"id": "<task id>"')) + planTask(2);
  assert.deepEqual(check('plan.md', text), { out: [], status: 0 });
  assert.deepEqual(read('plan.md', text, taskIds), ['T1', 'T2']);
});
