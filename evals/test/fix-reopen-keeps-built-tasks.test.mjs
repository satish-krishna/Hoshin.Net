// Self-test for fix-reopen-keeps-built-tasks: each grader is tried on what a redraft that changes only T2 leaves and on
// what a run that rewrote T1, touched the build log, or accepted again leaves. The typed reopen is the parity half.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, read, target } from './lib.mjs';
import { commonTests, edit, folderOf, reflogOf, withFix } from './fix-lib.mjs';

const CASE = 'fix-reopen-keeps-built-tasks';
const CHANGE = '2026-10-24-greeting-space';
const folder = folderOf(CHANGE);
const text = (ws, grader) => target(ws, CASE, grader);
const GOOD = [
  'redraft-committed-after-the-reopen', 'no-acceptance-after-the-reopen', 'plan-back-to-draft', 'spec-back-to-draft', 'plan-keeps-t1-as-written',
  'plan-t2-takes-the-feedback', 'spec-takes-the-feedback-and-keeps-the-named-greeting', 'build-log-unchanged', 'product-repository-unchanged',
];
// What a good run does: only the no-name greeting changes, in the spec's R1.2 and in T2 of the plan, committed through the program.
const REDRAFT = [
  'sed -i.bak "s/Welcome back\\./Welcome back!/g" "$(fix_folder)/spec.md" "$(fix_folder)/plan.md"',
  'rm "$(fix_folder)/spec.md.bak" "$(fix_folder)/plan.md.bak"',
  'commit_doc "$fix_change" fix-draft "intent/demo/$fix_change"',
].join(' && ');

commonTests(CASE, { fires: ['fix'] });

test(`${CASE}: the fixture is a two-task fix with T1 built and logged, reopened, and only the feedback graders fail on it`, () => {
  const ws = fixture(CASE);
  const log = read(ws, reflogOf(CHANGE));
  assert.match(log, /\tcommit: docs\(demo\): log T1 of 2026-10-24-greeting-space\n/);
  assert.match(log, /\tcommit: docs\(demo\): reopen fix 2026-10-24-greeting-space\n$/);
  assert.match(read(ws, `${folder}/plan.md`), /### T2: End the greeting without a name with a full stop/);
  for (const g of ['no-acceptance-after-the-reopen', 'plan-back-to-draft', 'spec-back-to-draft', 'plan-keeps-t1-as-written', 'build-log-unchanged', 'product-repository-unchanged']) {
    assert.ok(matches(CASE, g, text(ws, g)), `${g} holds on the reopened fixture`);
  }
  for (const g of ['redraft-committed-after-the-reopen', 'plan-t2-takes-the-feedback', 'spec-takes-the-feedback-and-keeps-the-named-greeting']) {
    assert.ok(!matches(CASE, g, text(ws, g)), `${g} must fail until the run applies the feedback`);
  }
});

test(`${CASE}: a redraft that changes only T2 passes every grader`, () => {
  const ws = fixture(CASE);
  withFix(ws, REDRAFT);
  for (const g of GOOD) assert.ok(matches(CASE, g, text(ws, g)), `${g} must pass on a good redraft:\n${text(ws, g).slice(0, 300)}`);
});

test(`${CASE}: T1 rewritten in any part, or dropped, fails plan-keeps-t1-as-written`, () => {
  const g = 'plan-keeps-t1-as-written';
  const original = text(fixture(CASE), g);
  assert.ok(matches(CASE, g, original));
  for (const [what, change] of [
    ['a reworded title', (t) => t.replace('### T1: Put a space after the comma', '### T1: Space after the comma')],
    ['another file list', (t) => t.replace('"files": ["greet.sh", "test.sh"]', '"files": ["greet.sh"]')],
    ['another scenario covered', (t) => t.replace('"covers": ["R1.1"]', '"covers": ["R1.1", "R1.2"]')],
    ['another repository', (t) => t.replace('"repo": "app"', '"repo": "web"')],
    ['a reworded change', (t) => t.replace('which removes the missing space named in the intent.', 'to add the space.')],
    ['a reworded test', (t) => t.replace('Run `bash test.sh`. It fails before', 'Run `bash test.sh`. It failed before')],
    ['a reworded done-when', (t) => t.replace('`bash test.sh` prints ok.\n\n### T2', '`bash test.sh` passes.\n\n### T2')],
    ['T1 dropped', (t) => t.replace(/### T1:[\s\S]*?(?=### T2:)/, '')],
  ]) {
    const changed = change(original);
    assert.notEqual(changed, original, what);
    assert.ok(!matches(CASE, g, changed), what);
  }
  // Whitespace inside the task's JSON block is the model's to format; the prose is not.
  assert.ok(matches(CASE, g, original.replace('{\n  "id": "T1",\n  "repo": "app",', '{ "id": "T1", "repo": "app",')));
});

test(`${CASE}: the build log grader fails on a log that gained an entry, lost its commit, or was rewritten`, () => {
  const ws = fixture(CASE);
  const g = 'build-log-unchanged';
  const log = text(ws, g);
  assert.ok(matches(CASE, g, log));
  assert.ok(!matches(CASE, g, `${log}\n### T2\n\n\`\`\`jsonc\n{\n  "task": "T2",\n  "commits": ["abc1234"]\n}\n\`\`\`\n`), 'a T2 entry');
  assert.ok(!matches(CASE, g, log.replace(/"commits": \["[0-9a-f]+"\]/, '"commits": []')), 'T1 without its commit');
  assert.ok(!matches(CASE, g, ''), 'the log removed');
  edit(ws, `${folder}/build/app.md`, (t) => t.replace('### T1', '### T1 again'));
  assert.ok(!matches(CASE, g, text(ws, g)));
});

test(`${CASE}: accepting again fails the no-acceptance grader, and the spec and plan graders fail on a feedback that was never applied`, () => {
  const ws = fixture(CASE);
  withFix(ws, `${REDRAFT}; sed -i.bak "s/\\"status\\": \\"draft\\"/\\"status\\": \\"accepted\\"/" "$(fix_folder)/spec.md" && rm "$(fix_folder)/spec.md.bak"; commit_doc "$fix_change" accept-spec "intent/demo/$fix_change/spec.md"`);
  assert.ok(!matches(CASE, 'no-acceptance-after-the-reopen', text(ws, 'no-acceptance-after-the-reopen')));
  assert.ok(!matches(CASE, 'spec-back-to-draft', text(ws, 'spec-back-to-draft')));
  const lost = fixture(CASE);
  edit(lost, `${folder}/spec.md`, (t) => t.replace('Then the greeting is "Welcome back, Mina"', 'Then the greeting is "Welcome back"').replace('Then the greeting is "Welcome back."', 'Then the greeting is "Welcome back!"'));
  assert.ok(!matches(CASE, 'spec-takes-the-feedback-and-keeps-the-named-greeting', text(lost, 'spec-takes-the-feedback-and-keeps-the-named-greeting')), 'the named greeting was changed too');
  edit(lost, `${folder}/plan.md`, (t) => t.replace('### T2: End the greeting without a name with a full stop', '### T2: End the greeting without a name with a full stop\n\nTBD'));
  assert.ok(!matches(CASE, 'plan-t2-takes-the-feedback', text(lost, 'plan-t2-takes-the-feedback')));
});

test(`${CASE}: the product repository grader fails once the app gains a commit`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'cd app && echo "# note" >> greet.sh && git_in commit -q -a -m "feat: build T2"');
  assert.ok(!matches(CASE, 'product-repository-unchanged', text(ws, 'product-repository-unchanged')));
});
