// Self-test for fix-reopens-an-accepted-fix: each grader is tried on what a good redraft of the reopened fix leaves and on
// what a run that did nothing, accepted again, or only changed one document leaves. The typed reopen is the parity half.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture, matches, read, target } from './lib.mjs';
import { commonTests, edit, folderOf, reflogOf, withFix } from './fix-lib.mjs';

const CASE = 'fix-reopens-an-accepted-fix';
const CHANGE = '2026-10-24-greeting-space';
const folder = folderOf(CHANGE);
const text = (ws, grader) => target(ws, CASE, grader);
const GOOD = [
  'redraft-committed-after-the-reopen', 'no-acceptance-after-the-reopen', 'intent-still-accepted', 'spec-back-to-draft', 'plan-back-to-draft',
  'spec-has-the-changed-scenario', 'plan-follows-the-changed-scenario', 'product-repository-unchanged',
];
// What a good run does: Mina becomes Zara in the spec and the plan, and the redraft is committed through the program.
const REDRAFT = 'sed -i.bak "s/Mina/Zara/g" "$(fix_folder)/spec.md" "$(fix_folder)/plan.md" && rm "$(fix_folder)/spec.md.bak" "$(fix_folder)/plan.md.bak" && commit_doc "$fix_change" fix-draft "intent/demo/$fix_change"';

commonTests(CASE, { fires: ['fix'] });

test(`${CASE}: the fixture is the accepted fix after a typed reopen, and only the feedback graders fail on it`, () => {
  const ws = fixture(CASE);
  const log = read(ws, reflogOf(CHANGE));
  assert.match(log, /\tcommit: docs\(demo\): accept plan 2026-10-24-greeting-space\n[^\n]*\tcommit: docs\(demo\): reopen fix 2026-10-24-greeting-space\n$/);
  for (const g of ['no-acceptance-after-the-reopen', 'intent-still-accepted', 'spec-back-to-draft', 'plan-back-to-draft', 'product-repository-unchanged']) {
    assert.ok(matches(CASE, g, text(ws, g)), `${g} holds on the reopened fixture`);
  }
  for (const g of ['redraft-committed-after-the-reopen', 'spec-has-the-changed-scenario', 'plan-follows-the-changed-scenario']) {
    assert.ok(!matches(CASE, g, text(ws, g)), `${g} must fail until the run applies the feedback`);
  }
});

test(`${CASE}: a redraft that applies the feedback to the spec and the plan passes every grader`, () => {
  const ws = fixture(CASE);
  withFix(ws, REDRAFT);
  for (const g of GOOD) assert.ok(matches(CASE, g, text(ws, g)), `${g} must pass on a good redraft:\n${text(ws, g).slice(0, 300)}`);
});

test(`${CASE}: accepting again fails the no-acceptance grader, and so do statuses that moved on`, () => {
  const ws = fixture(CASE);
  withFix(ws, `${REDRAFT}; ` + [
    'sed -i.bak "s/\\"status\\": \\"draft\\"/\\"status\\": \\"accepted\\"/" "$(fix_folder)/spec.md" && rm "$(fix_folder)/spec.md.bak"',
    'commit_doc "$fix_change" accept-spec "intent/demo/$fix_change/spec.md"',
  ].join('; '));
  assert.ok(!matches(CASE, 'no-acceptance-after-the-reopen', text(ws, 'no-acceptance-after-the-reopen')));
  assert.ok(!matches(CASE, 'spec-back-to-draft', text(ws, 'spec-back-to-draft')));
  assert.ok(matches(CASE, 'plan-back-to-draft', text(ws, 'plan-back-to-draft')), 'the plan is still a draft');
});

test(`${CASE}: the intent grader fails if the run sent the intent back to draft`, () => {
  const ws = fixture(CASE);
  edit(ws, `${folder}/intent.md`, (t) => t.replace('"status": "accepted"', '"status": "draft"'));
  assert.ok(!matches(CASE, 'intent-still-accepted', text(ws, 'intent-still-accepted')));
});

test(`${CASE}: the feedback graders fail when only the spec, or only the plan, changed, or a Mina is left behind`, () => {
  const ws = fixture(CASE);
  edit(ws, `${folder}/spec.md`, (t) => t.replaceAll('Mina', 'Zara'));
  assert.ok(matches(CASE, 'spec-has-the-changed-scenario', text(ws, 'spec-has-the-changed-scenario')));
  assert.ok(!matches(CASE, 'plan-follows-the-changed-scenario', text(ws, 'plan-follows-the-changed-scenario')), 'the plan still names Mina');
  edit(ws, `${folder}/plan.md`, (t) => t.replaceAll('Mina', 'Zara'));
  assert.ok(matches(CASE, 'plan-follows-the-changed-scenario', text(ws, 'plan-follows-the-changed-scenario')));
  edit(ws, `${folder}/spec.md`, (t) => `${t}\nOut of scope: a visitor named Mina.\n`);
  assert.ok(!matches(CASE, 'spec-has-the-changed-scenario', text(ws, 'spec-has-the-changed-scenario')), 'a leftover Mina');
});

test(`${CASE}: the product repository grader fails once the app gains a commit`, () => {
  const ws = fixture(CASE);
  withFix(ws, 'cd app && sed -i.bak \'s/Welcome back${2:+,$2}/Welcome back${2:+, $2}/\' greet.sh && rm greet.sh.bak && git_in commit -q -a -m "fix: space"');
  assert.ok(!matches(CASE, 'product-repository-unchanged', text(ws, 'product-repository-unchanged')));
});
