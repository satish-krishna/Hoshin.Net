// Helpers shared by the plan cases' self-tests (evals/README.md, B03 rows 28 to 38). They are not a test file, so
// `node --test "evals/test/*.test.mjs"` does not run them.
//
// A passing plan run ends the way the real one does: the model writes plan.md in the change's copy and ends its turn, and
// the Stop hook checks the file, pins it to the spec and commits it. runPlanTurn drives those same hooks with the payloads
// Claude Code sends, so the commit subject, the pin and the copy's reflog are the program's, not written by hand.
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { evals, read, withHelpers } from './lib.mjs';

export const CHANGE = '2026-11-01-greeting';
export const planPath = (ws, change = CHANGE) => join(ws, '.hoshin', 'copies', change, 'intent', 'demo', change, 'plan.md');
export const planRel = (change = CHANGE) => `.hoshin/copies/${change}/intent/demo/${change}/plan.md`;
export const changeReflog = (ws, change = CHANGE) => read(ws, `master/.git/worktrees/${change}/logs/HEAD`);
export const mainReflog = (ws) => read(ws, 'master/.git/logs/HEAD');

const skills = join(evals, '..', 'skills').replace(/\\/g, '/');

function payload(ws, event) {
  return JSON.stringify({ session_id: 'eval-session', transcript_path: '/tmp/eval-session.jsonl', cwd: ws, permission_mode: 'default', ...event });
}

/** The model's invocation of the plan skill, as the hooks see it. */
const startPayload = (ws, args) =>
  payload(ws, {
    hook_event_name: 'PostToolUse',
    tool_name: 'Skill',
    tool_use_id: 'toolu_plan',
    tool_input: { skill: 'hoshin:plan', args },
    tool_response: { success: true, commandName: 'hoshin:plan' },
  });

const stopPayload = (ws) => payload(ws, { hook_event_name: 'Stop', stop_hook_active: false, background_tasks: [], last_assistant_message: 'Done.' });

/**
 * Plays one plan turn through the program's hooks: the skill starts, the model writes `text` as plan.md in the change's
 * copy (nothing is written when `text` is null: a model that declined to write), and the turn ends. Returns what the Stop
 * hook answered ('' when it let the turn end quietly), so a test can see a block.
 */
export function runPlanTurn(ws, text, change = CHANGE) {
  writeFileSync(join(ws, 'payload-start.json'), startPayload(ws, change));
  writeFileSync(join(ws, 'payload-stop.json'), stopPayload(ws));
  withHelpers(ws, `hoshin hook start --roles "${skills}" < payload-start.json > /dev/null`);
  if (text !== null) writeFileSync(planPath(ws, change), text);
  return withHelpers(ws, `hoshin hook stop --roles "${skills}" < payload-stop.json`).trim();
}

const SCENARIOS = { basic: ['R1.1', 'R1.2', 'R2.1'], outline: ['R1.1'] };

/**
 * A plan the engine accepts for the fixtures' spec, in the layout the finished plan skill writes. `change` and `tests` are
 * the task's prose; `files` its files; `test` its argument list; `direction` and `checks` add their sections.
 */
export function planText({
  spec = 'basic',
  files = ['src/home.ts', 'src/home.test.ts'],
  change = 'The header greets returning visitors.',
  tests = 'A failing test in src/home.test.ts, run by the task\'s test command.',
  doneWhen = 'The task\'s test passes.',
  test = ['node', 'node_modules/vitest/vitest.mjs', 'run', 'src/home.test.ts'],
  direction,
  checks,
  risks = '- **Riskiest step:** T1.',
} = {}) {
  const covers = SCENARIOS[spec];
  const field = JSON.stringify({ id: 'T1', repo: 'app', depends_on: [], uses: [], files, covers, supports: [], test });
  const coverage = covers.map((id) => `| ${id} | a test of ${id} | T1 |`).join('\n');
  return [
    '```jsonc',
    `{ "layout": 2, "source_spec": "${CHANGE}/spec.md", "system": "demo", "date": "2026-11-01", "author": "eval", "status": "draft" }`,
    '```',
    '',
    '# Plan: Greet returning visitors',
    '',
    '## Approach',
    '',
    'One task changes the home header.',
    '',
    ...(direction === undefined ? [] : ['## Engineer direction', '', direction, '']),
    '## Scenario coverage',
    '',
    '| Scenario | Test | Task |',
    '|---|---|---|',
    coverage,
    '',
    '## Tasks',
    '',
    '### T1: Greet',
    '',
    '```jsonc',
    field,
    '```',
    '',
    '#### Change',
    '',
    change,
    '',
    '#### Tests',
    '',
    tests,
    '',
    '#### Done when',
    '',
    doneWhen,
    '',
    '## Order and dependencies',
    '',
    'One task.',
    '',
    ...(checks === undefined ? [] : ['## Checks at build finish', '', checks, '']),
    '## Risks',
    '',
    risks,
    '',
  ].join('\n');
}

/**
 * Replaces plan.md in the change's copy without running the hooks, so one fixture can show a grader many plans: only the
 * first run through runPlanTurn needs to prove the commit, and the content graders read the file.
 */
export const writePlan = (ws, text, change = CHANGE) => writeFileSync(planPath(ws, change), text);

/** A hand-run commit on main or in the copy, for the failing side of a reflog grader. */
export const strayCommit = (ws, repo, subject) =>
  withHelpers(ws, `cd ${repo} && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "${subject}"`);
