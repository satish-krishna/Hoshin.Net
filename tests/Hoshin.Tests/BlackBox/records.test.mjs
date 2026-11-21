import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { bareEnv, dotnet, program, testsRoot } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const part2Log = join(testsRoot, 'Fixtures', 'part-2', 'build', 'kata.md');
const FENCE = '```';

function run(args, cwd) {
  return spawnSync(dotnet, [program, ...args], {
    cwd: cwd ?? tmpdir(),
    env: bareEnv,
    encoding: 'utf8',
  });
}

function logFile(body, name = 'app.md') {
  const dir = tempDir('hoshin-records-');
  mkdirSync(join(dir, 'build'));
  const file = join(dir, 'build', name);
  writeFileSync(file, body);
  return file;
}

function read(file) {
  const r = run(['read', '--json', file]);
  assert.ok(r.status === 0 || r.status === 1, r.stderr);
  return JSON.parse(r.stdout);
}

const entry = (id, obj) => `### ${id}\n\n${FENCE}jsonc\n${JSON.stringify({ task: id, ...obj }, null, 2)}\n${FENCE}\n`;

test('a layout 2 departure with a divider and three lines is read whole and passes', () => {
  const departure = 'used a | in the header; see note\nsecond line with | again\nthird line';
  const body = `\`\`\`jsonc\n{ "layout": 2 }\n\`\`\`\n\n# Build log\n\n${entry('T7', { commits: ['2408179', 'ad61637'], departure })}`;
  const file = logFile(body);
  assert.equal(run(['check', file]).status, 0);
  const result = read(file);
  assert.equal(result.kind, 'build-log');
  assert.equal(result.layout, 2);
  assert.deepEqual(result.document.entries, [{ task: 'T7', commits: ['2408179', 'ad61637'], departure, red: null, line: 7 }]);
});

test('an entry with no departure reads as null, and the repo is the file name', () => {
  const file = logFile(`\`\`\`jsonc\n{ "layout": 2 }\n\`\`\`\n\n${entry('T1', { commits: ['abc1234'] })}`, 'billing.md');
  const result = read(file);
  assert.equal(result.document.repo, 'billing');
  assert.equal(result.document.entries[0].departure, null);
});

test('commit hashes read as text exactly as written, including all-digit ones', () => {
  const file = logFile(`\`\`\`jsonc\n{ "layout": 2 }\n\`\`\`\n\n${entry('T1', { commits: ['1234567', '0123456', '1e50000'] })}`);
  assert.deepEqual(read(file).document.entries[0].commits, ['1234567', '0123456', '1e50000']);
});

test('a table inside a fenced block is not a row', () => {
  const body = `\`\`\`jsonc\n{ "layout": 2 }\n\`\`\`\n\n${entry('T1', { commits: ['abc1234'], departure: '| T9 | zzz | none |' })}`;
  assert.deepEqual(
    read(logFile(body)).document.entries.map((e) => e.task),
    ['T1'],
  );
});

test('the converted Layout 2 part-2 build log reads as 30 entries with no problems', () => {
  const result = read(part2Log);
  assert.equal(result.layout, 2);
  assert.equal(result.document.entries.length, 30);
  assert.deepEqual(result.problems, []);
});

test('a review reads its sections, headers and findings', () => {
  const body = [
    '```jsonc',
    '{ "layout": 2 }',
    '```',
    '',
    '# Review findings',
    '',
    '## Review 1',
    '',
    `${FENCE}jsonc`,
    JSON.stringify({ review: 1, reviewed_at: '2026-10-22', commits: { kata: '840fa0c', hoshin: '1234567' } }, null, 2),
    FENCE,
    '',
    '### F1',
    '',
    `${FENCE}jsonc`,
    JSON.stringify({ id: 'F1', severity: 'high', file: 'app/src/x.css', line: 304 }, null, 2),
    FENCE,
    '',
    'The toolbar overflows.',
    '',
    'Second paragraph.',
    '',
    '## Review 2',
    '',
    `${FENCE}jsonc`,
    JSON.stringify({ review: 2, reviewed_at: '2026-10-23', commits: { kata: 'aaa1111' } }, null, 2),
    FENCE,
    '',
  ].join('\n');
  const file = logFile(body, 'review.md');
  assert.equal(run(['check', file]).status, 0);
  const result = read(file);
  assert.equal(result.kind, 'review');
  assert.deepEqual(result.document, {
    reviews: [
      {
        number: 1,
        reviewed_at: '2026-10-22',
        commits: { kata: '840fa0c', hoshin: '1234567' },
        findings: [
          {
            id: 'F1',
            severity: 'high',
            file: 'app/src/x.css',
            line: 304,
            text: 'The toolbar overflows.\n\nSecond paragraph.',
          },
        ],
      },
      { number: 2, reviewed_at: '2026-10-23', commits: { kata: 'aaa1111' }, findings: [] },
    ],
  });
});

test('an entry may say whether red was observed, in either form, and a made-up verdict is a problem', () => {
  const log = (red) => `\`\`\`jsonc\n{ "layout": 2 }\n\`\`\`\n\n# Build log: app\n\n${entry('T1', { commits: ['abc1234'], red })}`;
  assert.equal(run(['check', logFile(log('observed'))]).status, 0);
  assert.equal(run(['check', logFile(log('not observed: the test did not compile at abc1234'))]).status, 0);
  const maybe = run(['check', logFile(log('maybe'))]);
  assert.equal(maybe.status, 1);
  assert.match(maybe.stdout + maybe.stderr, /red/);
});

test("read returns an entry's red as logged, and null when the entry has none or it is not text", () => {
  const log = (extra) => `\`\`\`jsonc\n{ "layout": 2 }\n\`\`\`\n\n# Build log: app\n\n${entry('T1', { commits: ['abc1234'], ...extra })}`;
  const red = (extra) => read(logFile(log(extra))).document.entries[0].red;
  assert.equal(red({ red: 'observed' }), 'observed');
  assert.equal(red({ red: 'not observed: no test ran' }), 'not observed: no test ran');
  assert.equal(red({}), null);
  assert.equal(red({ red: '' }), null);
  assert.equal(red({ red: ['observed'] }), null);
  const made = read(logFile(log({ red: 'maybe' })));
  assert.equal(made.document.entries[0].red, 'maybe');
  assert.match(made.problems[0].message, /red/);
});
