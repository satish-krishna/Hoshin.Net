// Scratch folders for the black-box tests.
import { mkdtempSync, readdirSync, readFileSync, realpathSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const made = [];

process.on('exit', () => {
  for (const dir of made) {
    try {
      rmSync(dir, { recursive: true, force: true, maxRetries: 3, retryDelay: 50 });
    } catch (error) {
      process.stderr.write(`could not remove the scratch folder ${dir}: ${error.message}\n`);
    }
  }
});

export function tempDir(prefix) {
  const dir = realpathSync.native(mkdtempSync(join(realpathSync.native(tmpdir()), prefix)));
  made.push(dir);
  return dir;
}

export function testsCallingMkdtemp(dir) {
  const own = [fileURLToPath(import.meta.url), join(fileURLToPath(new URL('..', import.meta.url)), 'temp.test.mjs')];
  const found = [];
  const walk = (folder) => {
    for (const name of readdirSync(folder)) {
      const path = join(folder, name);
      if (statSync(path).isDirectory()) walk(path);
      else if (name.endsWith('.mjs') && !own.includes(path) && /mkdtemp/.test(readFileSync(path, 'utf8'))) found.push(path);
    }
  };
  walk(dir);
  return found;
}
