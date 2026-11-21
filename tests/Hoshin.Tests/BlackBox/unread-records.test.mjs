import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { bareEnv, dotnet, program, testsRoot } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const examples = join(testsRoot, 'Examples');

const CASES = [
  { kind: 'plan', name: 'quoted-record-key', rel: 'plan.md' },
  { kind: 'plan', name: 'task-heading-not-strict', rel: 'plan.md' },
  { kind: 'build-log', name: 'entry-heading-not-strict', rel: 'build/entry-heading-not-strict.md' },
  { kind: 'review', name: 'review-heading-not-strict', rel: 'build/review.md' },
  { kind: 'review', name: 'finding-heading-not-strict', rel: 'build/review.md' },
  { kind: 'review', name: 'no-review-section', rel: 'build/review.md' },
];

for (const c of CASES) {
  test(`check reports the record-shaped line that ${c.kind}/${c.name} would otherwise drop`, () => {
    const dir = tempDir('hoshin-unread-');
    mkdirSync(dirname(join(dir, c.rel)), { recursive: true });
    copyFileSync(join(examples, c.kind, '2', c.name + '.md'), join(dir, c.rel));
    const expected = JSON.parse(readFileSync(join(examples, c.kind, '2', c.name + '.expected.json'), 'utf8')).problems.map(
      (p) => `${p.file}:${p.line}: ${p.message}`
    );
    const r = spawnSync(dotnet, [program, 'check', c.rel], { cwd: dir, encoding: 'utf8', env: bareEnv });
    assert.equal(r.error, undefined);
    assert.deepEqual(r.stdout.replace(/\r\n/g, '\n').split('\n').filter((l) => l !== ''), expected);
    assert.equal(r.status, 1);
  });
}

function checkText(rel, text) {
  const dir = tempDir('hoshin-unread-');
  mkdirSync(dirname(join(dir, rel)), { recursive: true });
  writeFileSync(join(dir, rel), text);
  const r = spawnSync(dotnet, [program, 'check', rel], { cwd: dir, encoding: 'utf8', env: bareEnv });
  assert.equal(r.error, undefined);
  return { out: r.stdout.replace(/\r\n/g, '\n').split('\n').filter((l) => l !== ''), status: r.status };
}

test('an indented jsonc example outside any record is ordinary content', () => {
  const text = '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n# Plan\n\n- a list item\n\n  ```jsonc\n  { "note": "not a record" }\n  ```\n';
  assert.deepEqual(checkText('plan.md', text), { out: [], status: 0 });
});

const TASK = '```jsonc\n{\n  "id": "T1",\n  "repo": "app"\n}\n```\n\n#### Change\n\nChange it.\n\n#### Tests\n\nTest it first.\n\n#### Done when\n\nIt works.\n';

test('a plan with ordinary ### headings and a jsonc example inside a task section passes', () => {
  const text =
    '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n# Plan: Example\n\n## Approach\n\n### Testing strategy\n\nTests first.\n\n### Trade-offs\n\nNone.\n\n### Timeline\n\nSoon.\n\n## Tasks\n\n### T1: first\n\n' +
    TASK.replace('Change it.\n', 'Change it:\n\n```jsonc\n{ "ci": true }\n```\n');
  assert.deepEqual(checkText('plan.md', text), { out: [], status: 0 });
});

test('a review with ordinary ### headings and a jsonc example in a finding passes', () => {
  const text =
    '```jsonc\n{ "layout": 2 }\n```\n\n# Review findings\n\n## Review 1\n\n```jsonc\n{\n  "review": 1,\n  "reviewed_at": "2026-10-22",\n  "commits": { "kata": "840fa0c" }\n}\n```\n\n### F1\n\n```jsonc\n{\n  "id": "F1",\n  "severity": "low",\n  "file": "a.ts",\n  "line": 1\n}\n```\n\nSet it like this:\n\n```jsonc\n{ "ci": true }\n```\n\n### Follow-ups\n\nLater.\n';
  assert.deepEqual(checkText('build/review.md', text), { out: [], status: 0 });
});

const lineOf = (text, needle) => text.split('\n').findIndex((l) => l === needle) + 1;

test('a jsonc block directly under a record heading the reader did not take is still reported', () => {
  const text =
    '```jsonc\n{ "layout": 2 }\n```\n\n# Review findings\n\n## Review 1\n\n```jsonc\n{\n  "review": 1,\n  "reviewed_at": "2026-10-22",\n  "commits": { "kata": "840fa0c" }\n}\n```\n\n## Notes\n\n### F9\n\n```jsonc\n{\n  "id": "F9",\n  "severity": "low",\n  "file": "a.ts",\n  "line": 1\n}\n```\n\nOutside any review.\n';
  assert.deepEqual(checkText('build/review.md', text), { out: [`build/review.md:${lineOf(text, '### F9') + 2}: jsonc block is not part of any record`], status: 1 });
});

const PLAN_HEAD = '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n# Plan: Example\n\n## Tasks\n\n### T1: First task\n\n' + TASK + '\n';
const planBody = (id) => `\`\`\`jsonc\n{\n  "id": "${id}",\n  "repo": "app"\n}\n\`\`\`\n\n#### Change\n\nChange it.\n\n#### Tests\n\nTest it first.\n\n#### Done when\n\nIt works.\n`;
const LOG_HEAD = '```jsonc\n{ "layout": 2 }\n```\n\n# Build log\n\n## Entries\n\n### T1\n\n```jsonc\n{\n  "task": "T1",\n  "commits": ["abc1234"]\n}\n```\n\n';
const REVIEW_HEAD = '```jsonc\n{ "layout": 2 }\n```\n\n# Review findings\n\n## Review 1\n\n```jsonc\n{\n  "review": 1,\n  "reviewed_at": "2026-10-22",\n  "commits": { "kata": "840fa0c" }\n}\n```\n\n### F1\n\n```jsonc\n{\n  "id": "F1",\n  "severity": "low",\n  "file": "a.ts",\n  "line": 1\n}\n```\n\nA typo.\n\n';
const reviewBody = (id) => `\`\`\`jsonc\n{\n  "id": "${id}",\n  "severity": "low",\n  "file": "a.ts",\n  "line": 1\n}\n\`\`\`\n\nText.\n`;

const NEAR_MISSES = [
  { name: 'plan ### T12 - Second task', rel: 'plan.md', head: PLAN_HEAD, heading: '### T12 - Second task', body: planBody('T12'), what: 'a task heading' },
  { name: 'plan ## T2: x', rel: 'plan.md', head: PLAN_HEAD, heading: '## T2: x', body: planBody('T2'), what: 'a task heading' },
  { name: 'plan #### T12', rel: 'plan.md', head: PLAN_HEAD, heading: '#### T12', body: planBody('T12'), what: 'a task heading' },
  { name: 'build log ### T12: second', rel: 'build/kata.md', head: LOG_HEAD, heading: '### T12: second', body: '```jsonc\n{\n  "task": "T12",\n  "commits": ["abc1234"]\n}\n```\n', what: 'an entry heading' },
  { name: 'build log ## T2', rel: 'build/kata.md', head: LOG_HEAD, heading: '## T2', body: '```jsonc\n{\n  "task": "T2",\n  "commits": ["abc1234"]\n}\n```\n', what: 'an entry heading' },
  { name: 'review ### F12 (typo)', rel: 'build/review.md', head: REVIEW_HEAD, heading: '### F12 (typo)', body: reviewBody('F12'), what: 'a finding heading' },
  { name: 'review ## F2', rel: 'build/review.md', head: REVIEW_HEAD, heading: '## F2', body: reviewBody('F2'), what: 'a finding heading' },
  { name: 'review ## Review: 1', rel: 'build/review.md', head: REVIEW_HEAD, heading: '## Review: 1', body: '```jsonc\n{\n  "review": 2,\n  "reviewed_at": "2026-10-23",\n  "commits": { "kata": "aaa1111" }\n}\n```\n', what: 'a review heading' },
];

for (const c of NEAR_MISSES) {
  test(`a near-miss record heading is reported with its jsonc block: ${c.name}`, () => {
    const text = c.head + c.heading + '\n\n' + c.body;
    const line = lineOf(text, c.heading);
    assert.deepEqual(checkText(c.rel, text), {
      out: [`${c.rel}:${line}: heading "${c.heading}" is not ${c.what}`, `${c.rel}:${line + 2}: jsonc block is not part of any record`],
      status: 1,
    });
  });
}

test('a valid plan with tasks T1 to T12 passes', () => {
  let text = '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n# Plan: Example\n\n## Tasks\n\n';
  for (let n = 1; n <= 12; n++) text += `### T${n}: Task ${n}\n\n` + planBody('T' + n) + '\n';
  assert.deepEqual(checkText('plan.md', text), { out: [], status: 0 });
});

test('a valid review with findings F1 to F12 passes', () => {
  let text = REVIEW_HEAD.replace(/### F1\n[\s\S]*$/, '');
  for (let n = 1; n <= 12; n++) text += `### F${n}\n\n` + reviewBody('F' + n) + '\n';
  assert.deepEqual(checkText('build/review.md', text), { out: [], status: 0 });
});

test('a build log with ordinary headings and a jsonc example inside a change section passes', () => {
  const text = LOG_HEAD + '### Notes\n\nSome prose.\n\n#### Change\n\n```jsonc\n{ "ci": true }\n```\n';
  assert.deepEqual(checkText('build/kata.md', text), { out: [], status: 0 });
});

const FIELDS_ONLY = [
  { name: 'plan ### T2a: Second task', rel: 'plan.md', head: PLAN_HEAD, heading: '### T2a: Second task', body: planBody('T2') },
  { name: 'plan ### Task T2: Second task', rel: 'plan.md', head: PLAN_HEAD, heading: '### Task T2: Second task', body: planBody('T2') },
  { name: 'plan ### t2: Second task', rel: 'plan.md', head: PLAN_HEAD, heading: '### t2: Second task', body: planBody('T2') },
  { name: 'build log ### T2a', rel: 'build/kata.md', head: LOG_HEAD, heading: '### T2a', body: '```jsonc\n{\n  "task": "T2",\n  "commits": ["abc1234"]\n}\n```\n' },
  { name: 'review ### F2a', rel: 'build/review.md', head: REVIEW_HEAD, heading: '### F2a', body: reviewBody('F2') },
  { name: 'review ### f2', rel: 'build/review.md', head: REVIEW_HEAD, heading: '### f2', body: reviewBody('F2') },
  { name: 'review ## Review2', rel: 'build/review.md', head: REVIEW_HEAD, heading: '## Review2', body: '```jsonc\n{\n  "review": 2,\n  "reviewed_at": "2026-10-23",\n  "commits": { "kata": "aaa1111" }\n}\n```\n' },
];

for (const c of FIELDS_ONLY) {
  test(`an unread fields block is reported whatever heading is above it: ${c.name}`, () => {
    const text = c.head + c.heading + '\n\n' + c.body;
    const line = lineOf(text, c.heading);
    assert.deepEqual(checkText(c.rel, text), { out: [`${c.rel}:${line + 2}: jsonc block is not part of any record`], status: 1 });
  });
}

test('a jsonc block under a prose heading with no record key stays ordinary content', () => {
  const text = PLAN_HEAD + '### Notes\n\n```jsonc\n{ "note": "not a record" }\n```\n';
  assert.deepEqual(checkText('plan.md', text), { out: [], status: 0 });
});
