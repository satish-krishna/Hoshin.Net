import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readdirSync, readFileSync, existsSync, statSync, mkdirSync, copyFileSync, cpSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { bareEnv, dotnet, program, testsRoot } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const examples = join(testsRoot, 'Examples');

function stage(c) {
  const dir = tempDir('hoshin-example-');
  const name = basename(c.doc);
  const rel = { spec: 'spec.md', plan: 'plan.md', review: 'build/review.md', 'build-log': `build/${name}` }[c.kind];
  if (rel === undefined) return null;
  mkdirSync(dirname(join(dir, rel)), { recursive: true });
  copyFileSync(c.doc, join(dir, rel));
  return { dir, rel };
}

function walk() {
  const cases = [];
  if (!existsSync(examples)) return cases;
  for (const kind of readdirSync(examples)) {
    const kindDir = join(examples, kind);
    if (!statSync(kindDir).isDirectory()) continue;
    for (const layout of readdirSync(kindDir)) {
      const layoutDir = join(kindDir, layout);
      if (!statSync(layoutDir).isDirectory()) continue;
      for (const name of readdirSync(layoutDir)) {
        if (!name.endsWith('.md')) continue;
        const doc = join(layoutDir, name);
        const expected = doc.replace(/\.md$/, '.expected.json');
        cases.push({ kind, layout, doc, expected });
      }
    }
  }
  return cases;
}

test('every example has an expected result and read --json matches it', () => {
  assert.equal(existsSync(program), true, 'hoshin.dll is missing');
  const cases = walk();
  assert.ok(cases.length > 0, 'no examples found');
  for (const c of cases) {
    assert.ok(existsSync(c.expected), `missing ${c.expected}`);
    const expected = JSON.parse(readFileSync(c.expected, 'utf8'));
    const staged = stage(c);
    if (staged === null) continue;
    const result = spawnSync(dotnet, [program, 'read', '--json', staged.rel], {
      cwd: staged.dir,
      encoding: 'utf8',
      env: bareEnv,
    });
    assert.equal(result.status === 0 || result.status === 1, true, result.stderr);
    const actual = JSON.parse(result.stdout);
    assert.deepEqual(actual, expected, c.doc);
  }
});

test('every change example checks to its expected problems', () => {
  const changeDir = join(examples, 'change');
  assert.equal(existsSync(program), true, 'hoshin.dll is missing');
  let seen = 0;
  for (const layout of readdirSync(changeDir)) {
    for (const name of readdirSync(join(changeDir, layout))) {
      const src = join(changeDir, layout, name);
      if (!statSync(src).isDirectory()) continue;
      seen++;
      const dir = tempDir('hoshin-change-example-');
      cpSync(src, dir, { recursive: true });
      const files = ['spec.md', 'plan.md'].filter((f) => existsSync(join(dir, f)));
      if (existsSync(join(dir, 'build'))) {
        for (const f of readdirSync(join(dir, 'build')).sort()) if (f.endsWith('.md')) files.push(`build/${f}`);
      }
      const result = spawnSync(dotnet, [program, 'check', ...files], {
        cwd: dir,
        encoding: 'utf8',
        env: bareEnv,
      });
      const actual = result.stdout
        .replace(/\r\n/g, '\n')
        .split('\n')
        .filter((l) => l !== '')
        .map((l) => {
          const m = /^(.*?):(\d+): (.*)$/.exec(l);
          return { file: m[1], line: Number(m[2]), message: m[3] };
        });
      const expected = JSON.parse(readFileSync(join(src, 'expected.json'), 'utf8')).problems;
      assert.deepEqual(actual, expected, src);
      assert.equal(result.status, expected.length === 0 ? 0 : 1, src);
    }
  }
  assert.equal(seen > 0, true, 'no change examples found');
});
