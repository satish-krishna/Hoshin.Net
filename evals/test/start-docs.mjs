// What a light-track draft of the greeting bug leaves in a change's copy, for the start cases' self-tests: the three
// documents a passing run's fix skill writes, committed through the program as `docs(demo): draft fix <change>`.
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { withHelpers } from './lib.mjs';

export const CHANGE = '2026-11-01-greeting-space';

const fence = (obj) => '```jsonc\n' + JSON.stringify(obj, null, 2) + '\n```';

export function fixDocuments(change = CHANGE, { track = 'fix', pin = 'pending acceptance' } = {}) {
  const intent =
    `${fence({ title: 'Fix the missing space in the greeting', system: 'demo', author: 'Eve, engineer', date: '2026-11-01', status: 'draft', scope: 'demo', ...(track ? { track } : {}), kind: 'bug', context: ['app@abc1234'] })}\n\n` +
    '# Intent: Fix the missing space in the greeting\n\n## Problem\n\nA returning visitor named Mina sees "Welcome back,Mina". Context rule C1 says the greeting is "Welcome back, " followed by the name. The cause is `greet.sh:4`, which prints `,$2` with no space.\n\n' +
    '## Proposed outcome\n\nThe greeting reads "Welcome back, Mina".\n\n## Open questions\n\nNone.\n';
  const spec =
    `${fence({ layout: 2, source_intent: `${change}/intent.md`, source_intent_commit: pin, system: 'demo', date: '2026-11-01', status: 'draft', policies_applied: [], policies_not_checked: ['security'] })}\n\n` +
    '# Spec: Fix the missing space in the greeting\n\n## Requirements\n\n```gherkin\nFeature: Fix the missing space in the greeting\n  The greeting puts a space after the comma.\n\n  @R1 @from:problem\n  Rule: The greeting has a space after the comma\n\n    @R1.1\n    Scenario: A returning visitor named Mina\n      Given a returning visitor with the first name "Mina"\n      When the greeting is printed\n      Then it reads "Welcome back, Mina"\n```\n\n## Design\n\nThe greeting string in greet.sh.\n';
  const plan =
    `${fence({ layout: 2, source_spec: `${change}/spec.md`, source_spec_commit: pin, system: 'demo', date: '2026-11-01', author: 'Eve', status: 'draft' })}\n\n` +
    '# Plan: Fix the missing space in the greeting\n\n## Approach\n\nCorrect the greeting string in greet.sh, line 4.\n\n## Scenario coverage\n\n| Scenario | Test | Task |\n|---|---|---|\n| R1.1 | the greeting reads Welcome back, Mina | T1 |\n\n## Tasks\n\n### T1: Put the space back\n\n' +
    `${fence({ id: 'T1', repo: 'app', depends_on: [], uses: [], files: ['greet.sh', 'test.sh'], covers: ['R1.1'], supports: [] })}\n\n` +
    '#### Change\n\nPrint a space after the comma in `greet.sh`.\n\n#### Tests\n\nAssert that `bash greet.sh returning Mina` prints "Welcome back, Mina".\n\n#### Done when\n\nThe test passes.\n';
  return { intent, spec, plan };
}

/** Starts the change through the program, writes the three documents in its copy and commits them as the draft. */
export function draftFix(ws, change = CHANGE, options) {
  withHelpers(ws, `change_start ${change} fix demo`);
  const folder = join(ws, '.hoshin', 'copies', change, 'intent', 'demo', change);
  mkdirSync(folder, { recursive: true });
  const docs = fixDocuments(change, options);
  for (const [name, text] of Object.entries(docs)) writeFileSync(join(folder, `${name}.md`), text);
  withHelpers(ws, `cd "$(copy_of ${change})" && hoshin commit --json --kind fix-draft --change ${change} -- intent/demo/${change}/intent.md intent/demo/${change}/spec.md intent/demo/${change}/plan.md | grep -q '"committed":true'`);
}

/** Hook trace lines, one per decision, as the hooks write them while a run is active (decision = "<skill>: <step>"). */
export const traceOf = (...decisions) =>
  decisions
    .map((decision) => JSON.stringify({ at: '2026-11-26T00:00:00Z', harness: 'claude-code', command: 'hook start', event: 'PostToolUse', sessionId: 's1', tool: 'Skill', ms: 12, decision, output: null }) + '\n')
    .join('');

/** A second draft commit on the same change, through the program: the plan changes and is committed again. */
export function redraftFix(ws, change = CHANGE) {
  const plan = join(ws, '.hoshin', 'copies', change, 'intent', 'demo', change, 'plan.md');
  withHelpers(ws, `printf '\\nA second draft.\\n' >> "${plan.replace(/\\/g, '/')}" && cd "$(copy_of ${change})" && hoshin commit --json --kind fix-draft --change ${change} -- intent/demo/${change}/plan.md | grep -q '"committed":true'`);
}

/** The change's reflog in the copy, and the paths of the documents the graders read, relative to the workspace. */
export const copyLog = (change = CHANGE) => `master/.git/worktrees/${change}/logs/HEAD`;
export const documentPath = (name, change = CHANGE) => `.hoshin/copies/${change}/intent/demo/${change}/${name}.md`;
