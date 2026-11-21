// The tests' scratch folders: `tempDir` makes one and removes it when the test file's process ends, and no test makes
// one any other way. A run of the tests used to leave hundreds of folders in the machine's temporary folder.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { testsCallingMkdtemp } from './helpers/temp.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const helper = join(here, 'helpers', 'temp.mjs');

// Runs a script that makes scratch folders with the helper and prints them, and returns the folders it printed.
function made(body) {
  const script = `import { tempDir } from ${JSON.stringify(pathToFileURL(helper).href)};
import { chmodSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
${body}`;
  const ran = spawnSync(process.execPath, ['--input-type=module', '-e', script], { encoding: 'utf8' });
  return { status: ran.status, stderr: ran.stderr, dirs: ran.stdout.split(/\r?\n/).filter(Boolean) };
}

test('a scratch folder exists while the process runs and is gone when it ends', () => {
  const ran = made(`const dir = tempDir('hoshin-temp-');
mkdirSync(join(dir, 'a', 'b'), { recursive: true });
writeFileSync(join(dir, 'a', 'b', 'file'), 'x');
chmodSync(join(dir, 'a', 'b', 'file'), 0o444);
console.log(dir);
console.log(tempDir('hoshin-temp-'));`);
  assert.equal(ran.status, 0, ran.stderr);
  assert.equal(ran.dirs.length, 2);
  assert.notEqual(ran.dirs[0], ran.dirs[1]);
  for (const dir of ran.dirs) {
    assert.match(dir, /hoshin-temp-/);
    assert.equal(existsSync(dir), false, `${dir} is still there`);
  }
});

test('a scratch folder is removed when the process ends with a failure', () => {
  const ran = made(`console.log(tempDir('hoshin-temp-'));
process.exit(3);`);
  assert.equal(ran.status, 3);
  assert.equal(ran.dirs.length, 1);
  assert.equal(existsSync(ran.dirs[0]), false);
});

test('no test of the program makes a scratch folder without the helper', () => {
  assert.deepEqual(testsCallingMkdtemp(here), []);
});

test('a file that calls mkdtemp is found, in a folder below the one searched too', () => {
  const ran = made(`const dir = tempDir('hoshin-temp-');
mkdirSync(join(dir, 'below'));
writeFileSync(join(dir, 'clean.mjs'), 'export const a = 1;');
writeFileSync(join(dir, 'notes.txt'), 'mkdtempSync');
writeFileSync(join(dir, 'below', 'leaks.test.mjs'), 'import { mkdtempSync } from "node:fs";');
const { testsCallingMkdtemp } = await import(${JSON.stringify(pathToFileURL(helper).href)});
for (const file of testsCallingMkdtemp(dir)) console.log(file.slice(dir.length + 1).replaceAll(String.fromCharCode(92), '/'));`);
  assert.equal(ran.status, 0, ran.stderr);
  assert.deepEqual(ran.dirs, ['below/leaks.test.mjs']);
});
