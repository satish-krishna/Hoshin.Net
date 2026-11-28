// Checks CLIENTS.md against Hoshin.Net CLI usage and C# source / JSON schemas.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { bareEnv, dotnet, program, repoRoot } from './helpers/workspace.mjs';

const clientsMdPath = existsSync(join(repoRoot, 'CLIENTS.md'))
  ? join(repoRoot, 'CLIENTS.md')
  : 'D:/workspaces/hoshin/tools/CLIENTS.md';

const contract = readFileSync(clientsMdPath, 'utf8').split(/\r?\n/);

function programText() {
  const skip = new Set(['bin', 'obj']);
  const parts = [];
  const walk = (dir) => {
    for (const name of readdirSync(dir)) {
      const path = join(dir, name);
      if (statSync(path).isDirectory()) {
        if (!skip.has(name)) walk(path);
      } else if (/\.cs$|\.schema\.json$/.test(name)) {
        parts.push(readFileSync(path, 'utf8'));
      }
    }
  };
  walk(join(repoRoot, 'src'));
  return parts.join('\n');
}

const spans = (line) => [...line.matchAll(/`([^`]+)`/g)].map((m) => m[1]);

function sections() {
  const found = [];
  for (const line of contract) {
    if (line.startsWith('## ')) found.push({ title: line.slice(3), lines: [] });
    else if (found.length > 0) found.at(-1).lines.push(line);
  }
  return found;
}

const listed = (kind) => sections().flatMap((s) => s.lines.filter((l) => l.startsWith(`- ${kind}: `)).flatMap(spans).map((item) => ({ item, section: s.title })));

const usage = spawnSync(dotnet, [program], { encoding: 'utf8', env: bareEnv });
const usageText = usage.stdout + usage.stderr;
const source = programText().replaceAll('\\"', '"');

const usageCommands = new Set(
  [...usageText.matchAll(/(?:hoshin(?:\.dll|\.ts)?) +([a-z-]+)(?: +(\S+))?/g)].flatMap(([, command, next]) =>
    next && /^[a-z]+(?:\|[a-z]+)*$/.test(next) ? next.split('|').map((sub) => `${command} ${sub}`) : [command],
  ),
);
const wordIn = (text, word) => new RegExp(`(?<![\\w-])${word.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?![\\w-])`).test(text);
const fieldIn = (text, name) => wordIn(text, name) || wordIn(text, name[0].toUpperCase() + name.slice(1));

test('every command the contract lists is a command of the program', () => {
  const commands = sections().map((s) => /^`([a-z-]+(?: [a-z]+)?)`$/.exec(s.title)?.[1]).filter(Boolean);
  assert.ok(commands.length >= 19, `only ${commands.length} command sections were found`);
  for (const command of commands) assert.ok(usageCommands.has(command), `the usage text has no ${command}`);
});

test('a subcommand the usage text writes as alternatives counts as each of them', () => {
  assert.ok(usageCommands.has('red show') && usageCommands.has('red record'));
  assert.ok(!usageCommands.has('red'), 'red alone is not a command');
  assert.ok(usageCommands.has('change start') && usageCommands.has('change show'));
});

test('every option the contract lists is in the usage text', () => {
  const options = listed('Options');
  assert.ok(options.length > 50, 'the Options lines were not found');
  for (const { item, section } of options) {
    assert.match(item, /^--[a-z-]+$/, `${section}: ${item} is not an option`);
    assert.ok(wordIn(usageText, item), `${section}: the usage text has no ${item}`);
  }
});

test('every field and value the contract lists still appears in the program or its schemas', () => {
  const fields = listed('Fields');
  const values = listed('Values');
  assert.ok(fields.length > 150 && values.length > 50, 'the Fields and Values lines were not found');
  for (const { item, section } of fields) {
    for (const name of item.split('.')) assert.ok(fieldIn(source, name), `${section}: no ${name} (of ${item}) in the program`);
  }
  for (const { item, section } of values) assert.ok(wordIn(source, item), `${section}: no ${item} in the program`);
});

test('every message text the contract lists is still written by the program', () => {
  const section = sections().find((s) => s.title === 'Message text');
  const texts = section.lines.filter((l) => l.startsWith('- ')).flatMap((l) => spans(l));
  assert.ok(texts.length >= 9, `only ${texts.length} message texts were found`);
  for (const text of texts) assert.ok(source.includes(text), `the program no longer writes: ${text}`);
});
