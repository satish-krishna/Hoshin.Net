// Checks every case's shape, and that README's B03 table, the case folders and the parity tests agree. The runner
// refuses a case whose YAML doesn't parse, and finding that out costs a paid run, so the shape is checked here first.
// Run with: node --test "evals/test/*.test.mjs"
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { caseText, cases, evals, graders } from './lib.mjs';

const KNOWN_GRADERS = new Set(['regex', 'tool_used', 'tool_order', 'file_exists', 'llm', 'baseline']);
const parityDir = join(evals, '..', 'tests', 'Hoshin.Tests', 'Acceptance', 'Parity');
const parityText = () =>
  existsSync(parityDir)
    ? readdirSync(parityDir).filter((f) => f.endsWith('.cs')).map((f) => readFileSync(join(parityDir, f), 'utf8')).join('\n')
    : '';

// README's B03 table: one row per original case, "| <n> | <case> | <destination> | <task> | <why> |".
function sortingTable() {
  const readme = readFileSync(join(evals, 'README.md'), 'utf8');
  const section = readme.slice(readme.indexOf('## Where each original case went (B03)'), readme.indexOf('Cases the original never had'));
  return [...section.matchAll(/^\| +(\d+) \| ([a-z0-9-]+) \| ([^|]+) \|/gm)].map((m) => ({ n: Number(m[1]), name: m[2], destination: m[3].trim() }));
}

function newCasesTable() {
  const readme = readFileSync(join(evals, 'README.md'), 'utf8');
  const section = readme.slice(readme.indexOf('Cases the original never had'), readme.indexOf('## Grader self-tests'));
  return [...section.matchAll(/^\| ([a-z0-9-]+) \| ([^|]+) \|/gm)].map((m) => ({ name: m[1], destination: m[2].trim() })).filter((r) => r.name !== 'case');
}

// The case files stay in a narrow YAML subset, so it can be checked without a YAML library: two-space indentation,
// block scalars only under `prompt: |`, and every other value a quoted string, a flow list, a number, a boolean or a
// bare word with no ": " and no " #" in it (the two ways a plain scalar turns into something else).
function shapeProblems(name) {
  const problems = [];
  let block = -1;
  caseText(name).split('\n').forEach((line, i) => {
    const at = `${name}/case.yaml:${i + 1}`;
    if (line.trim() === '') return;
    const indent = line.match(/^ */)[0].length;
    if (block >= 0 && indent > block) return;
    block = -1;
    if (/\t/.test(line)) problems.push(`${at}: a tab`);
    if (indent % 2) problems.push(`${at}: odd indentation`);
    const m = line.match(/^ *(?:- )?([a-z_]+):(?: (.*))?$/);
    if (!m) return problems.push(`${at}: not "key: value": ${line}`);
    const value = (m[2] ?? '').trim();
    if (value === '|') {
      if (m[1] !== 'prompt') problems.push(`${at}: a block scalar outside prompt`);
      block = indent;
      return;
    }
    if (value === '' || /^'(?:[^']|'')*'$/.test(value) || /^"(?:[^"\\]|\\.)*"$/.test(value)) return;
    if (/^\[.*\]$/.test(value)) {
      if (/(^|, *)[^"'\s[][^,\]]*: /.test(value.slice(1, -1))) problems.push(`${at}: a flow list item with ": "`);
      return;
    }
    if (/^-?\d+(\.\d+)?$/.test(value) || /^(true|false)$/.test(value)) return;
    if (/: | #|^[&*!|>%@`'"]/.test(value)) problems.push(`${at}: a plain scalar YAML would misread: ${value}`);
  });
  return problems;
}

test('every case has a case.yaml in the runner\'s subset, and a fixture.sh', () => {
  const problems = cases().flatMap(shapeProblems);
  for (const name of cases()) if (!existsSync(join(evals, name, 'fixture.sh'))) problems.push(`${name}: no fixture.sh`);
  assert.deepEqual(problems, []);
});

test('every case names itself, uses schema 1.1, and tests the built bundle', () => {
  for (const name of cases()) {
    const text = caseText(name);
    assert.match(text, /^schema_version: "1\.1"$/m, name);
    assert.match(text, new RegExp(`^name: ${name}$`, 'm'), `${name}: name is not the folder's`);
    // run.sh makes the built bundle the target, with the cases below it; the command refuses a plugins: entry outside the case's folder.
    assert.doesNotMatch(text, /^plugins:/m, `${name}: names a plugin, but the bundle under test is the run's target`);
    assert.match(text, /^ {2}scaffold_script: fixture\.sh$/m, `${name}: no scaffold_script`);
    assert.match(text, /^ {2}prompt: \|$/m, `${name}: the prompt is not a block scalar`);
  }
});

test('every case carries hook-trace-has-an-entry, so a dead hook fails it (B02 item 2)', () => {
  for (const name of cases()) {
    const g = graders(name)['hook-trace-has-an-entry'];
    assert.ok(g, `${name}: no hook-trace-has-an-entry grader`);
    assert.equal(g.type, 'regex', name);
    assert.equal(g.path, 'master/.git/hoshin/hooks.jsonl', name);
  }
});

test('every grader has a known type, and every pattern compiles as the runner compiles it', () => {
  for (const name of cases()) {
    for (const [grader, g] of Object.entries(graders(name))) {
      assert.ok(KNOWN_GRADERS.has(g.type), `${name}/${grader}: type ${g.type}`);
      for (const key of ['pattern', 'input_match', 'before', 'after']) {
        if (g[key] !== undefined) assert.doesNotThrow(() => new RegExp(g[key], key === 'pattern' ? g.flags ?? '' : 'm'), `${name}/${grader}.${key}`);
      }
    }
  }
});

test('no case grades the original\'s mechanism: hoshin.ts, YAML front matter, should_match', () => {
  for (const name of cases()) {
    const text = caseText(name);
    assert.doesNotMatch(text, /hoshin\.ts/, `${name}: names hoshin.ts`);
    assert.doesNotMatch(text, /should_match/, `${name}: should_match (use match: not_contains)`);
    assert.doesNotMatch(text, /'[^'\n]*\bstatus: (?:draft|accepted)\b/, `${name}: a YAML front-matter pattern; documents are JSONC ("status": "draft")`);
    assert.doesNotMatch(text, /\\A|\\z|\\Z/, `${name}: \\A, \\z and \\Z are not JavaScript regex anchors`);
  }
});

test('README\'s B03 table has one row for each of the original\'s 56 cases, in order', () => {
  const rows = sortingTable();
  assert.equal(rows.length, 56);
  assert.deepEqual(rows.map((r) => r.n), Array.from({ length: 56 }, (_, i) => i + 1));
  assert.equal(new Set(rows.map((r) => r.name)).size, 56, 'a case is listed twice');
});

test('every LLM row has a case folder, and every case folder has a row', () => {
  const llm = [...sortingTable(), ...newCasesTable()].filter((r) => /LLM|probe/.test(r.destination)).map((r) => r.name);
  const missing = llm.filter((name) => !existsSync(join(evals, name, 'case.yaml')));
  assert.deepEqual(missing, [], 'LLM rows with no case folder');
  const listed = new Set(llm);
  assert.deepEqual(cases().filter((name) => !listed.has(name)), [], 'case folders with no LLM row');
});

test('every Parity row has a parity test that names its original case', () => {
  const text = parityText();
  const missing = sortingTable()
    .filter((r) => /Parity/.test(r.destination))
    .map((r) => r.name)
    .filter((name) => !text.includes(`[Trait("Original", "${name}")]`));
  assert.deepEqual(missing, [], 'Parity rows with no [Trait("Original", "<case>")] under tests/Hoshin.Tests/Acceptance/Parity/');
});

test('every dropped row names an accepted difference that TASKS.md lists', () => {
  const tasks = readFileSync(join(evals, '..', 'docs', 'review', 'TASKS.md'), 'utf8');
  const accepted = tasks.slice(tasks.indexOf('### Accepted differences'), tasks.indexOf('## R3-rules'));
  for (const row of sortingTable().filter((r) => /^Dropped/.test(r.destination))) {
    assert.ok(accepted.includes(row.name), `${row.name} is dropped, but no Accepted differences row names it`);
  }
});

test('every case has its own self-test file', () => {
  const tests = new Set(readdirSync(join(evals, 'test')).filter((f) => f.endsWith('.test.mjs')));
  assert.deepEqual(cases().filter((name) => !tests.has(`${name}.test.mjs`)), []);
});
