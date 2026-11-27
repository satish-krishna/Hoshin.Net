// Shared helpers for the grader self-tests (B03 item 4). A grader that can't match the right behavior turns a working
// skill red, and one that matches anything lets a broken skill pass, so each case's graders are tried on both sides:
// against what a passing run leaves behind, and against what a failing run leaves behind.
//
// A self-test builds the case's real fixture in a scratch folder (fixture.sh, run with bash, the program pointed at the
// test build), makes the commits a passing (or failing) run would make through the program itself, and runs the case's
// patterns over the result. Run with: node --test "evals/test/*.test.mjs"
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tempDir } from '../../tests/Hoshin.Tests/BlackBox/helpers/temp.mjs';
import { dotnet, program } from '../../tests/Hoshin.Tests/BlackBox/helpers/workspace.mjs';

export const evals = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export { program };

/** Every case folder (one with a case.yaml). */
export const cases = () => readdirSync(evals).filter((entry) => existsSync(join(evals, entry, 'case.yaml'))).sort();

export const caseText = (name) => readFileSync(join(evals, name, 'case.yaml'), 'utf8').replace(/\r\n/g, '\n');

/**
 * The graders of one case.yaml, by name: { type, input_match?, pattern?, flags?, match?, path?, before?, after? }.
 * Every pattern in these files is a single-quoted YAML scalar on one line, where '' stands for one quote; a pattern
 * written any other way is reported rather than misread. A tool_order grader's two patterns are kept as before/after.
 */
export function graders(name) {
  const found = {};
  let current;
  let side;
  for (const line of caseText(name).split('\n')) {
    const named = line.match(/^ {2}- name: (\S+)\s*$/);
    if (named) [current, side] = [(found[named[1]] = {}), undefined];
    if (!current) continue;
    const order = line.match(/^ {4}(before|after):\s*$/);
    if (order) side = order[1];
    const type = line.match(/^ {4}type: (\S+)\s*$/);
    if (type) current.type = type[1];
    const field = line.match(/^ {4}(input_match|pattern): (.*)$/) ?? (side && line.match(/^ {6}(input_match): (.*)$/));
    if (field) {
      const quoted = field[2].match(/^'((?:[^']|'')*)'\s*$/);
      assert.ok(quoted, `${name}: ${field[1]} is not a single-quoted scalar: ${field[2]}`);
      current[line.startsWith('      ') ? side : field[1]] = quoted[1].replace(/''/g, "'");
    }
    const scalar = line.match(/^ {4,6}(flags|match|path|tool|exists|min|max): (.*?)\s*$/);
    if (scalar && !(scalar[1] === 'path' && line.startsWith('      ') && current.path)) current[scalar[1]] = scalar[2].replace(/^"(.*)"$/, '$1');
  }
  return found;
}

/**
 * True when the named grader's pattern matches text. A regex grader compiles with the flags its case declares; a
 * tool_used grader matches the tool input as JSON, tried multiline, the way the runner sees it. A not_contains
 * grader passes when its pattern does not match; this returns the raw match, so a test that such a grader fires is
 * written as matches() true on the forbidden text.
 */
export function matches(name, grader, text) {
  const g = graders(name)[grader];
  assert.ok(g, `${name} has no grader ${grader}`);
  if (g.pattern !== undefined) return new RegExp(g.pattern, g.flags ?? '').test(text);
  assert.ok(g.input_match !== undefined, `${name}/${grader} has no pattern to try`);
  return new RegExp(g.input_match, 'm').test(text);
}

/** The JSON a tool_used grader sees for a Bash call, a Skill call, or a Write. */
export const bash = (command) => JSON.stringify({ command });
export const skill = (name, args) => JSON.stringify(args === undefined ? { skill: name } : { skill: name, args });
export const writeCall = (file_path, content) => JSON.stringify({ file_path, content });

// Git Bash on Windows, never WSL's bash.exe in System32, which would run the fixture in another file system.
function findBash() {
  if (process.env.EVAL_BASH) return process.env.EVAL_BASH;
  if (process.platform !== 'win32') return 'bash';
  const where = spawnSync('where', ['git'], { encoding: 'utf8' });
  const gitExe = where.stdout?.split(/\r?\n/).find((l) => l.trim());
  const candidate = gitExe && join(dirname(dirname(gitExe.trim())), 'bin', 'bash.exe');
  if (candidate && existsSync(candidate)) return candidate;
  return 'C:\\Program Files\\Git\\bin\\bash.exe';
}

export const bashExe = findBash();

const env = {
  ...process.env,
  EVAL_HOSHIN_DLL: program,
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  DOTNET_NOLOGO: '1',
  GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null',
  GIT_CONFIG_NOSYSTEM: '1',
  PATH: [dirname(dotnet), process.env.PATH].join(process.platform === 'win32' ? ';' : ':'),
};

/** Runs a bash script (text) in cwd with the fixtures' environment; fails the test on a non-zero exit. */
export function sh(cwd, script) {
  const r = spawnSync(bashExe, ['-c', script], { cwd, encoding: 'utf8', env });
  assert.equal(r.status, 0, `bash in ${cwd} failed (${r.status}):\n${script}\n--- stdout ---\n${r.stdout}\n--- stderr ---\n${r.stderr}`);
  return r.stdout;
}

/**
 * Builds a case's fixture in a fresh scratch folder and returns the folder, which is the case's workspace (master/,
 * app/ and, once a change starts, .hoshin/copies/<change>/). The folder is removed when the test file ends.
 */
export function fixture(name) {
  const dir = tempDir(`eval-${name}-`);
  const script = join(evals, name, 'fixture.sh').replace(/\\/g, '/');
  sh(dir, `bash "${script}"`);
  return dir;
}

/**
 * Runs bash in the workspace with the shared fixture's helpers loaded (hoshin, change_start, copy_of, commit_doc),
 * so a self-test makes the commits a run would make through the program, as the fixtures do. The helpers file would
 * build a fresh base workspace when sourced, so only its function definitions are taken.
 */
export function withHelpers(workspace, script) {
  const shared = join(evals, 'shared', 'workspace.sh').replace(/\\/g, '/');
  const prelude = [
    'set -euo pipefail',
    `evals_dir="${evals.replace(/\\/g, '/')}"`,
    `workspace_root="$(pwd)"`,
    // The function definitions only: everything from the first line that builds the workspace is left out.
    `eval "$(sed -n '/^git_in()/,/^mkdir master app$/p' "${shared}" | sed '$d')"`,
  ].join('\n');
  return sh(workspace, `${prelude}\n${script}`);
}

/** A file in the workspace, or '' when it doesn't exist (a grader on a missing file sees no text). */
export function read(workspace, rel) {
  const path = join(workspace, rel);
  return existsSync(path) ? readFileSync(path, 'utf8').replace(/\r\n/g, '\n') : '';
}

/** The text a regex grader with a file target sees: the file at the grader's target path, read from the workspace. */
export function target(workspace, name, grader) {
  const g = graders(name)[grader];
  assert.ok(g?.path, `${name}/${grader} has no target path`);
  return read(workspace, g.path);
}

/** The hook trace line a run leaves when the hooks ran: what hook-trace-has-an-entry looks for. */
export const traceLine = (event = 'UserPromptSubmit') =>
  JSON.stringify({ at: '2026-11-26T00:00:00Z', harness: 'claude-code', command: 'hook start', event, sessionId: 's1', tool: null, ms: 12, decision: null, output: null }) + '\n';
