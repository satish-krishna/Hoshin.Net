import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bareEnv, dotnet, program } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

function run(text, ...args) {
  const dir = tempDir('hoshin-specunread-');
  writeFileSync(join(dir, 'spec.md'), text);
  const r = spawnSync(dotnet, [program, ...args, 'spec.md'], { cwd: dir, encoding: 'utf8', env: bareEnv });
  assert.equal(r.error, undefined);
  return r;
}
const check = (text) => {
  const r = run(text, 'check');
  return { out: r.stdout.replace(/\r\n/g, '\n').split('\n').filter((l) => l !== ''), status: r.status };
};
const ids = (text) => JSON.parse(run(text, 'read', '--json').stdout).document.requirements.map((q) => q.id);
const lineOf = (text, needle) => text.split(/\r\n|\r|\n/).findIndex((l) => l.trim() === needle || l.trim().startsWith(needle + ' @')) + 1;
const unread = (text, tag, what) => `spec.md:${lineOf(text, tag)}: ${what} ${tag.slice(1)} is not part of the requirements the reader found`;

const HEAD = '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n# Spec\n\n';
const R1 = (extra = '') =>
  `  @R1 @from:D1\n  Rule: One\n\n    @R1.1\n    Scenario: First\n      Given a thing\n${extra}\n`;
const R2 = `  @R2 @from:D1\n  Rule: Two\n\n    @R2.1\n    Scenario: Second\n      Given a thing\n\n    @R2.2\n    Scenario: Third\n      Given a thing\n\n`;
const R3 = `  @R3 @from:D1\n  Rule: Three\n\n    @R3.1\n    Scenario: Fourth\n      Given a thing\n`;
const body = (...rules) => `Feature: Demo\n\n${rules.join('')}`;
const fenced = (open, content, close = open.replace(/[^`~].*$/, '')) => `${open}\n${content}${close}\n`;
const spec = (content, open = '```gherkin') => HEAD + fenced(open, content);

for (const open of ['```Gherkin', '``` gherkin', '```GHERKIN ', '~~~gherkin', '````gherkin']) {
  test(`a block opened as "${open}" is read`, () => {
    const text = spec(body(R1(), R2, R3), open);
    assert.deepEqual(check(text), { out: [], status: 0 });
    assert.deepEqual(ids(text), ['R1', 'R2', 'R3']);
  });
}

test('a layout 2 spec with no gherkin block is reported', () => {
  const text = HEAD + 'No requirements here.\n\n```ts\nconst x = 1;\n```\n';
  assert.deepEqual(check(text), { out: ['spec.md:1: no gherkin requirements block found'], status: 1 });
});

test('a doc string closed by a bare fence ends the block and the unread tags are reported', () => {
  const docString = '      Given text\n      ```\n      some text\n      ```\n';
  const text = spec(body(R1(docString), R2, R3));
  assert.deepEqual(ids(text), ['R1']);
  const out = ['@R2', '@R2.1', '@R2.2', '@R3', '@R3.1'].map((t) => unread(text, t, t.includes('.') ? 'scenario' : 'requirement'));
  assert.deepEqual(check(text), { out, status: 1 });
});

test('a second gherkin block with new requirements is reported', () => {
  const text = spec(body(R1(), R2)) + '\nMore:\n\n' + fenced('```gherkin', body(R3));
  assert.deepEqual(check(text), {
    out: [unread(text, '@R3', 'requirement'), unread(text, '@R3.1', 'scenario')],
    status: 1,
  });
});

test('a second gherkin block quoting a requirement that was read passes', () => {
  const quote = 'Quoted:\n\n' + fenced('```gherkin', '  @R2 @from:D1\n  Rule: Two\n\n    @R2.1\n    Scenario: Second\n');
  const text = spec(body(R1(), R2)) + '\n' + quote;
  assert.deepEqual(check(text), { out: [], status: 0 });
});

test('an @R tag in prose that names a requirement that was read passes, and one that was not is reported', () => {
  const ok = spec(body(R1(), R2)) + '\nSee @R2 and @R2.1.\n';
  assert.deepEqual(check(ok), { out: [], status: 0 });
  const bad = spec(body(R1(), R2)) + '\nSee @R9.\n';
  assert.deepEqual(check(bad), { out: [`spec.md:${lineOf(bad, 'See @R9.')}: requirement R9 is not part of the requirements the reader found`], status: 1 });
});

test('a scenario left under no Rule because the first Rule line was deleted is reported', () => {
  const text = spec(body(R1().replace('  Rule: One\n', ''), R2, R3));
  assert.deepEqual(check(text), { out: [`spec.md:${lineOf(text, 'Scenario: First')}: scenario R1.1 sits under no requirement`], status: 1 });
  assert.deepEqual(ids(text), ['R2', 'R3']);
});

test('a tagged scenario written straight under the Feature is reported', () => {
  const stray = '  @R0.1\n  Scenario: Stray\n    Given a thing\n\n';
  const text = spec(body(stray, R1(), R2));
  assert.deepEqual(check(text), { out: [`spec.md:${lineOf(text, 'Scenario: Stray')}: scenario R0.1 sits under no requirement`], status: 1 });
});
