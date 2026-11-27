// Self-test for context-refreshes-a-stale-file: each grader is tried on what a passing run leaves and on what a failing run leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, graders, matches, program, read, sh, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'context-refreshes-a-stale-file';
const FILE = 'master/intent/demo/context/app.md';
const on = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));
const g = (name, text) => matches(CASE, name, text);
const sha = () => /"source_commit"\\s\*:\\s\*"([0-9a-f]{40})"/.exec(graders(CASE)['source-commit-moved-to-the-apps-head'].pattern)[1];

// What a passing run writes: C1 and C3 carried as they were, C2 and C4 retired, C5 never reused, and the changed and the
// new behavior under C6 and C7, tagged as an engineer who is away allows.
const goodFile = (head) => `\`\`\`jsonc
{ "repo": "app", "system": "demo", "source_commit": "${head}", "built": "2026-11-26", "built_by": "Eve Engineer" }
\`\`\`

# Context: app

The app greets visitors and says goodbye to the ones who leave.

## Rules the system enforces

| # | Rule | Provenance | Note |
|---|---|---|---|
| C1 | A returning visitor who gave a first name sees "Welcome back, " followed by that name | confirmed by engineer | Eve confirmed on the 2026-09-02 call |
| C3 | A returning visitor who gave no first name sees "Welcome back" | intent unknown | Ask Pat in product whether a missing name should show a different greeting |
| C6 | A first-time visitor sees the greeting "Welcome" | intent unknown | Replaces C2, which said a first-time visitor sees no greeting; the code changed and nobody could say if that is wanted |
| C7 | A returning visitor who leaves sees "See you soon" | seen in code | |

## Known oddities

None.
`;
const report = 'Refreshed the context for app. Rules carried forward unchanged: 2 (C1, C3). Changed after asking: 1 (C2 became C6). Added: 1 (C7). Removed: 1 (C4).';

test(`${CASE}: the skill grader sees context, plain or namespaced`, () => {
  assert.ok(g('skill-fired', skill('hoshin:context')));
  assert.ok(g('skill-fired', skill('context')));
  assert.ok(!g('skill-fired', skill('hoshin:review')));
});

test(`${CASE}: the fixture is stale by two commits, keeps a retired C5 in its history, and starts with a build and a refresh committed`, () => {
  const ws = fixture(CASE);
  assert.equal(withHelpers(ws, 'git -C app rev-parse HEAD').trim(), sha(), 'the pinned source_commit is the fixture\'s app HEAD, on every machine');
  const stale = JSON.parse(sh(ws, `dotnet "${program.replace(/\\/g, '/')}" stale --json --system demo`)).repos[0];
  assert.deepEqual([stale.stale, stale.behind, stale.reason], [true, 2, 'behind'], 'two product commits after the context file\'s source_commit');
  const history = withHelpers(ws, 'git -C master log -p --format=%s -- intent/demo/context/app.md');
  assert.match(history, /^-\| C5 \|/m, 'C5 was retired by the refresh in the fixture');
  assert.doesNotMatch(read(ws, FILE), /^\| C5 \|/m, 'the file at HEAD no longer has C5');
  for (const row of ['C1', 'C2', 'C3', 'C4']) assert.match(read(ws, FILE), new RegExp(`^\\| ${row} \\|`, 'm'));
  assert.ok(!on(ws, 'committed-once-by-the-library'), 'the fixture has two context commits, not three');
  assert.ok(on(ws, 'product-repository-unchanged'));
  assert.ok(!on(ws, 'hook-trace-has-an-entry'));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(on(ws, 'hook-trace-has-an-entry'));
});

test(`${CASE}: a refresh committed through the program passes the reflog grader once, and a hand commit or a second commit fails it`, () => {
  const ws = fixture(CASE);
  writeFileSync(join(ws, FILE), goodFile(sha()));
  withHelpers(ws, `cd master && hoshin commit --json --kind context --system demo -- intent/demo/context/app.md | grep -q '"committed":true'`);
  assert.ok(on(ws, 'committed-once-by-the-library'), 'refresh context for app, the third context commit');
  withHelpers(ws, 'cd master && printf "\\nA later edit.\\n" >> intent/demo/context/app.md && hoshin commit --json --kind context --system demo -- intent/demo/context/app.md | grep -q \'"committed":true\'');
  assert.ok(!on(ws, 'committed-once-by-the-library'), 'a fourth commit');
  const hand = fixture(CASE);
  writeFileSync(join(hand, FILE), goodFile(sha()));
  withHelpers(hand, 'cd master && git_in commit -q -am "docs(demo): update context"');
  assert.ok(!on(hand, 'committed-once-by-the-library'), 'a hand commit has no library subject');
});

test(`${CASE}: the good refresh passes every file grader, and the not_contains graders do not match it`, () => {
  const text = goodFile(sha());
  for (const grader of ['front-matter-is-fresh', 'source-commit-moved-to-the-apps-head', 'unchanged-confirmed-rule-carried-with-its-note', 'unchanged-unknown-rule-carried-with-its-note', 'new-behavior-of-first-time-visitors-is-intent-unknown', 'its-note-names-the-earlier-rule', 'new-farewell-is-seen-in-code-under-a-new-number']) {
    assert.ok(g(grader, text), grader);
  }
  for (const grader of ['changed-rule-is-retired', 'rule-whose-code-is-gone-is-not-carried-as-seen-in-code', 'retired-number-is-not-reused', 'nothing-new-is-confirmed-without-asking']) {
    assert.ok(!g(grader, text), `${grader} is a not_contains grader: its pattern must not match the good file`);
  }
});

test(`${CASE}: the front matter must be fresh, and source_commit must be the app's HEAD`, () => {
  const text = goodFile(sha());
  assert.ok(!g('front-matter-is-fresh', text.replace('"2026-11-26"', '"2026-10-01"')), 'built left at the old date');
  assert.ok(!g('front-matter-is-fresh', text.replace(', "built_by": "Eve Engineer"', '')), 'no built_by');
  assert.ok(!g('source-commit-moved-to-the-apps-head', text.replace(sha(), '463b8ff8c4fbc6192a4d90659e5f85c967e99b68')), 'source_commit left at the old commit');
  assert.ok(!g('source-commit-moved-to-the-apps-head', text.replace(sha(), sha().slice(0, 7))), 'an abbreviated commit');
});

test(`${CASE}: carried rules must keep their number, tag and note, and a changed or vanished rule must not survive as it was`, () => {
  const text = goodFile(sha());
  assert.ok(!g('unchanged-confirmed-rule-carried-with-its-note', text.replace('Eve confirmed on the 2026-09-02 call', '')), 'C1\'s note dropped');
  assert.ok(!g('unchanged-confirmed-rule-carried-with-its-note', text.replace('| C1 | A returning visitor who gave a first name sees "Welcome back, " followed by that name | confirmed by engineer', '| C1 | A returning visitor who gave a first name sees "Welcome back, " followed by that name | seen in code')), 'C1 downgraded');
  assert.ok(!g('unchanged-confirmed-rule-carried-with-its-note', text.replace('| C1 |', '| C2 |')), 'C1 renumbered');
  assert.ok(!g('unchanged-unknown-rule-carried-with-its-note', text.replace('Ask Pat in product', 'Ask someone')), 'C3\'s note dropped');
  assert.ok(!g('unchanged-unknown-rule-carried-with-its-note', text.replace('| C3 |', '| C2 |')), 'C3 renumbered');
  assert.ok(g('changed-rule-is-retired', text.replace('| C3 |', '| C2 | A first-time visitor sees no greeting | confirmed by engineer | Eve confirmed on the 2026-09-02 call |\n| C3 |')), 'C2 kept');
  assert.ok(g('rule-whose-code-is-gone-is-not-carried-as-seen-in-code', text.replace('| C6 |', '| C4 | On weekends every visitor sees a "Weekend sale" banner | seen in code | |\n| C6 |')), 'C4 kept as seen in code');
  assert.ok(!g('rule-whose-code-is-gone-is-not-carried-as-seen-in-code', text.replace('| C6 |', '| C4 | On weekends every visitor sees a "Weekend sale" banner | intent unknown | The banner code is gone |\n| C6 |')), 'C4 flagged rather than carried as clear is not what this grader forbids');
  assert.ok(g('retired-number-is-not-reused', text.replace('| C7 |', '| C5 |')), 'C5 reused');
});

test(`${CASE}: the changed and the new behavior must be recorded under new numbers with the right tags`, () => {
  const text = goodFile(sha());
  assert.ok(!g('new-behavior-of-first-time-visitors-is-intent-unknown', text.replace('| C6 | A first-time visitor sees the greeting "Welcome" | intent unknown', '| C6 | A first-time visitor sees the greeting "Welcome" | seen in code')), 'a changed behavior taken for clear');
  assert.ok(!g('new-behavior-of-first-time-visitors-is-intent-unknown', text.replace('| C6 |', '| C2 |')), 'the changed rule under its old number');
  assert.ok(!g('new-behavior-of-first-time-visitors-is-intent-unknown', text.replace(/^\| C6 .*\n/m, '')), 'the new behavior never recorded');
  assert.ok(!g('its-note-names-the-earlier-rule', text.replace('Replaces C2, which said a first-time visitor sees no greeting; the code changed and nobody could say if that is wanted', 'Nobody could say')), 'a note that does not say what the rule was');
  assert.ok(g('its-note-names-the-earlier-rule', text.replace('Replaces C2, which said a first-time visitor sees no greeting; the code changed and nobody could say if that is wanted', 'The rule was that a first-time visitor sees no greeting')), 'another wording of the earlier rule');
  assert.ok(!g('new-farewell-is-seen-in-code-under-a-new-number', text.replace(/^\| C7 .*\n/m, '')), 'the farewell never recorded');
  assert.ok(!g('new-farewell-is-seen-in-code-under-a-new-number', text.replace('| C7 | A returning visitor who leaves sees "See you soon" | seen in code', '| C7 | A returning visitor who leaves sees "See you soon" | intent unknown')), 'the farewell, clear in code, left unknown');
  assert.ok(!g('new-farewell-is-seen-in-code-under-a-new-number', text.replace('| C7 |', '| C4 |')), 'the farewell under a retired number');
});

test(`${CASE}: nothing is confirmed by engineer except what an earlier engineer confirmed`, () => {
  const text = goodFile(sha());
  assert.ok(g('nothing-new-is-confirmed-without-asking', text.replace('| intent unknown | Replaces C2', '| confirmed by engineer | Replaces C2')), 'the changed rule confirmed');
  assert.ok(g('nothing-new-is-confirmed-without-asking', text.replace('| seen in code | |\n\n## Known', '| confirmed by engineer | |\n\n## Known')), 'the new rule confirmed');
  assert.ok(g('nothing-new-is-confirmed-without-asking', text.replace('None.', '| The old banner | confirmed by engineer | a bug |')), 'an oddity confirmed');
});

test(`${CASE}: the report must count the carried, changed, added and removed rules, and not a miscount`, () => {
  for (const grader of ['report-counts-the-carried-rules', 'report-counts-the-changed-rule', 'report-counts-the-added-rule', 'report-counts-the-removed-rule']) assert.ok(g(grader, report), grader);
  for (const ok of ['2 carried forward, 1 changed after asking, 1 added, 1 removed.', 'Carried: two. Changed: one. Added: one. Removed: one.', '| carried | 2 |\n| changed | 1 |\n| added | 1 |\n| removed | 1 |']) {
    for (const grader of ['report-counts-the-carried-rules', 'report-counts-the-changed-rule', 'report-counts-the-added-rule', 'report-counts-the-removed-rule']) assert.ok(g(grader, ok), `${grader}: ${ok}`);
  }
  const miscount = '3 carried forward, 0 changed, 1 added, 0 removed.';
  assert.ok(!g('report-counts-the-carried-rules', miscount), 'three carried');
  assert.ok(!g('report-counts-the-changed-rule', miscount), 'zero changed');
  assert.ok(g('report-counts-the-added-rule', miscount), 'one added');
  assert.ok(!g('report-counts-the-removed-rule', miscount), 'zero removed, next to one added');
  assert.ok(!g('report-counts-the-carried-rules', 'Refreshed the context for app.'), 'no counts');
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(g('no-hand-commit', bash('git -C master commit -am "docs(demo): refresh context"')));
  assert.ok(!g('no-hand-commit', bash('git -C app log --oneline')));
});
