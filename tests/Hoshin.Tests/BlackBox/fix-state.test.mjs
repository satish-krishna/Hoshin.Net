import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { dotnet, env, program } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const CHANGE = '2026-10-01-fix-typo';
const FOLDER = `intent/demo/${CHANGE}`;

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

function repo({ autocrlf = false } = {}) {
  const dir = tempDir('hoshin-fix-state-');
  git(dir, 'init', '-q');
  git(dir, 'config', 'user.name', 'Test');
  git(dir, 'config', 'user.email', 'test@example.com');
  git(dir, 'config', 'commit.gpgsign', 'false');
  git(dir, 'config', 'core.autocrlf', autocrlf ? 'true' : 'false');
  writeFileSync(join(dir, '.hoshin.json'), JSON.stringify({ version: 1, systems: { demo: { repos: ['app'] } } }) + '\n');
  git(dir, 'add', '--', '.hoshin.json');
  git(dir, 'commit', '-q', '-m', 'chore: set up the master repository', '--', '.hoshin.json');
  mkdirSync(join(dir, FOLDER, 'build'), { recursive: true });
  return dir;
}

const fence = (obj) => '```jsonc\n' + JSON.stringify(obj, null, 2) + '\n```';

function intent(status, { track = 'fix', extra = {} } = {}) {
  const fm = {
    title: 'Fix the greeting typo',
    system: 'demo',
    author: 'Test, engineer',
    date: '2026-10-01',
    status,
    scope: 'greeting',
    ...(track === null ? {} : { track }),
    kind: 'bug',
    context: ['app@abc1234'],
    ...extra,
  };
  return (
    `${fence(fm)}\n\n` +
    `# Intent: Fix the greeting typo\n\n## Problem\n\nThe greeting reads "Helo".\n\n## Proposed outcome\n\nThe greeting reads "Hello".\n\n## Open questions\n\nNone.\n`
  );
}

function spec(status, pin = 'pending acceptance') {
  return (
    `${fence({
      layout: 2,
      source_intent: `${CHANGE}/intent.md`,
      source_intent_commit: pin,
      system: 'demo',
      date: '2026-10-01',
      status,
      policies_applied: [],
      policies_not_checked: ['security'],
    })}\n\n` +
    `# Spec: Fix the greeting typo\n\n## Requirements\n\n\`\`\`gherkin\nFeature: Fix the greeting typo\n  The greeting reads "Hello".\n\n  @R1 @from:problem\n  Rule: The greeting is spelled correctly\n\n    @R1.1\n    Scenario: The greeting is shown\n      Given a visitor\n      When the page opens\n      Then the greeting reads "Hello"\n\`\`\`\n\n## Design\n\nThe string in src/greeting.ts.\n`
  );
}

function plan(status, pin = 'pending acceptance', { layout = 2 } = {}) {
  return (
    `${fence({
      layout,
      source_spec: `${CHANGE}/spec.md`,
      source_spec_commit: pin,
      system: 'demo',
      date: '2026-10-01',
      author: 'Test',
      status,
    })}\n\n` +
    `# Plan: Fix the greeting typo\n\n## Approach\n\nCorrect the string.\n\n## Scenario coverage\n\n| Scenario | Test | Task |\n|---|---|---|\n| R1.1 | greeting reads Hello | T1 |\n\n## Tasks\n\n### T1: Fix the greeting\n\n${fence({
      id: 'T1',
      repo: 'app',
      depends_on: [],
      uses: [],
      files: ['src/greeting.ts'],
      covers: ['R1.1'],
      supports: [],
    })}\n\n#### Change\n\nFix it.\n\n#### Tests\n\nTest it.\n\n#### Done when\n\nIt passes.\n`
  );
}

const BUILD_LOG =
  '```jsonc\n{ "layout": 2 }\n```\n\n# Build log: app\n\n### T1\n\n```jsonc\n{\n  "task": "T1",\n  "commits": ["abc1234"],\n  "departure": null\n}\n```\n';

const DOCS = { intent: 'intent.md', spec: 'spec.md', plan: 'plan.md' };
const write = (dir, name, text) => writeFileSync(join(dir, FOLDER, name), text);
const read = (dir, name) => readFileSync(join(dir, FOLDER, name), 'utf8');

function commit(dir, subject, names) {
  const paths = names.map((n) => `${FOLDER}/${n}`);
  git(dir, 'add', '--', ...paths);
  git(dir, 'commit', '-q', '-m', subject, '--', ...paths);
}

function draft(dir) {
  write(dir, DOCS.intent, intent('draft'));
  write(dir, DOCS.spec, spec('draft'));
  write(dir, DOCS.plan, plan('draft'));
  commit(dir, `docs(demo): draft fix ${CHANGE}`, Object.values(DOCS));
}

function acceptedText(kind) {
  if (kind === 'intent') return intent('accepted');
  if (kind === 'spec') return spec('accepted', '1111111111111111111111111111111111111111');
  return plan('accepted', '2222222222222222222222222222222222222222');
}

function accept(dir, kind) {
  write(dir, DOCS[kind], acceptedText(kind));
  commit(dir, `docs(demo): accept ${kind} ${CHANGE}`, [DOCS[kind]]);
}

function fixState(dir, folder = join(dir, FOLDER)) {
  return spawnSync(dotnet, [program, 'fix-state', '--json', folder], {
    cwd: dir,
    env,
    encoding: 'utf8',
  });
}

function expectState(dir, expected) {
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), expected);
}

const doc = (inHead, headStatus, workingStatus) => ({ inHead, headStatus, workingStatus });

test('row 1: an intent rejected in HEAD closes the fix', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.intent, intent('rejected'));
  commit(dir, `docs(demo): reject intent ${CHANGE}`, [DOCS.intent]);
  expectState(dir, {
    row: 1,
    facts: {
      intent: doc(true, 'rejected', 'rejected'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 2: a superseded plan is a status the workflow never leaves on a fix', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.plan, plan('superseded'));
  commit(dir, `docs(demo): supersede plan ${CHANGE}`, [DOCS.plan]);
  expectState(dir, {
    row: 2,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'superseded', 'superseded'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 2: the spec accepted without the intent is not a leading run', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'spec');
  expectState(dir, {
    row: 2,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'accepted', 'accepted'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'other',
      acceptanceLast: true,
      built: false,
    },
  });
});

test('row 2: a document not in HEAD while another is accepted in HEAD', () => {
  const dir = repo();
  write(dir, DOCS.intent, intent('draft'));
  write(dir, DOCS.spec, spec('draft'));
  commit(dir, `docs(demo): draft fix ${CHANGE}`, [DOCS.intent, DOCS.spec]);
  accept(dir, 'intent');
  write(dir, DOCS.plan, plan('draft'));
  expectState(dir, {
    row: 2,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(false, null, 'draft'),
      unrecorded: ['plan'],
      ownLinesOnly: false,
      accepted: 'leading',
      acceptanceLast: true,
      built: false,
    },
  });
});

test('row 3: a document not in HEAD and not written, while none is accepted', () => {
  const dir = repo();
  write(dir, DOCS.intent, intent('draft'));
  write(dir, DOCS.spec, spec('draft'));
  commit(dir, `docs(demo): draft fix ${CHANGE}`, [DOCS.intent, DOCS.spec]);
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(false, null, null),
      unrecorded: ['plan'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 3: a drafting run that stopped before its commit leaves documents not in HEAD', () => {
  const dir = repo();
  write(dir, DOCS.intent, intent('draft'));
  write(dir, DOCS.spec, spec('draft'));
  write(dir, DOCS.plan, plan('draft'));
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(false, null, 'draft'),
      spec: doc(false, null, 'draft'),
      plan: doc(false, null, 'draft'),
      unrecorded: ['intent', 'spec', 'plan'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 3: an edit not yet recorded', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.spec, spec('draft') + '\nOne more line.\n');
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: ['spec'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 3: a half-written status: rejected is not own lines', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.intent, intent('rejected'));
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'draft', 'rejected'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: ['intent'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 3: an uncommitted status: accepted that also lost its final newline', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.intent, intent('accepted').replace(/\n$/, ''));
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'draft', 'accepted'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: ['intent'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 3: own lines on the intent with another document edited too', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.intent, intent('accepted'));
  write(dir, DOCS.plan, plan('draft') + '\nAnd a note.\n');
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'draft', 'accepted'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: ['plan'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 3: a status: accepted written on a document that is not the first unaccepted one', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.spec, spec('accepted', '1111111111111111111111111111111111111111'));
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'draft', 'accepted'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: ['spec'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 3: a change to a document accepted in HEAD', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  accept(dir, 'spec');
  accept(dir, 'plan');
  write(dir, DOCS.spec, acceptedText('spec') + '\nEdited after acceptance.\n');
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'accepted', 'accepted'),
      plan: doc(true, 'accepted', 'accepted'),
      unrecorded: ['spec'],
      ownLinesOnly: false,
      accepted: 'all',
      acceptanceLast: true,
      built: false,
    },
  });
});

test('row 3: a line ending git would record is a difference', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.plan, plan('draft').replace(/\n/g, '\r\n'));
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: ['plan'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 4: an uncommitted status: accepted on the intent is own lines', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.intent, intent('accepted'));
  expectState(dir, {
    row: 4,
    facts: {
      intent: doc(true, 'draft', 'accepted'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: true,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 4: accepted_in_advance may be added with the status', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.intent, intent('accepted', { extra: { accepted_in_advance: true } }));
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.row, 4);
  assert.equal(out.facts.ownLinesOnly, true);
});

test('row 4: the spec status and pin after an accepted intent', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  write(dir, DOCS.spec, acceptedText('spec'));
  expectState(dir, {
    row: 4,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'draft', 'accepted'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: true,
      accepted: 'leading',
      acceptanceLast: true,
      built: false,
    },
  });
});

test('row 3: with core.autocrlf=false, the first unaccepted document converted to CRLF and accepted is not own lines', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.intent, intent('accepted').replace(/\n/g, '\r\n'));
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'draft', 'accepted'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: ['intent'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 4: with core.autocrlf=true, a CRLF working copy accepted is own lines', () => {
  const dir = repo({ autocrlf: true });
  draft(dir);
  accept(dir, 'intent');
  for (const name of Object.values(DOCS)) rmSync(join(dir, FOLDER, name));
  git(dir, 'checkout', '--', FOLDER);
  write(dir, DOCS.spec, acceptedText('spec').replace(/\n/g, '\r\n'));
  write(dir, DOCS.plan, plan('draft').replace(/\n/g, '\r\n'));
  assert.equal(git(dir, 'diff', 'HEAD', '--stat', '--', `${FOLDER}/plan.md`), '');
  expectState(dir, {
    row: 4,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'draft', 'accepted'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: true,
      accepted: 'leading',
      acceptanceLast: true,
      built: false,
    },
  });
});

test('row 4: front matter keys followed by spaces are still own lines', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  write(dir, DOCS.spec, spec('draft').replace('"status": "draft"', '"status" : "accepted"').replace('pending acceptance', '1111111'));
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.row, 4);
  assert.equal(out.facts.ownLinesOnly, true);
});

test('row 3: a status: accepted line outside the front matter is not own lines', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.intent, intent('accepted') + '\n"status": "accepted"\n');
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.row, 3);
  assert.deepEqual(out.facts.unrecorded, ['intent']);
});

test('row 5: the intent accepted, an acceptance commit last', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  expectState(dir, {
    row: 5,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'leading',
      acceptanceLast: true,
      built: false,
    },
  });
});

test('row 5: the intent and the spec accepted, an acceptance commit last', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  accept(dir, 'spec');
  expectState(dir, {
    row: 5,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'accepted', 'accepted'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'leading',
      acceptanceLast: true,
      built: false,
    },
  });
});

test('row 6: all three drafted, none accepted, nothing built', () => {
  const dir = repo();
  draft(dir);
  expectState(dir, {
    row: 6,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 6: with core.autocrlf=true, LF documents checked out as CRLF are not a difference', () => {
  const dir = repo({ autocrlf: true });
  draft(dir);
  for (const name of Object.values(DOCS)) rmSync(join(dir, FOLDER, name));
  git(dir, 'checkout', '--', FOLDER);
  if (process.platform === 'win32') {
    assert.ok(read(dir, DOCS.intent).includes('\r\n'));
  }
  assert.equal(git(dir, 'show', `HEAD:${FOLDER}/intent.md`).includes('\r'), false);
  expectState(dir, {
    row: 6,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 6: with core.autocrlf=true, a document an editor rewrites with CRLF is not a difference', () => {
  const dir = repo({ autocrlf: true });
  draft(dir);
  write(dir, DOCS.spec, spec('draft').replace(/\n/g, '\r\n'));
  assert.match(git(dir, 'status', '--porcelain=v1', '--', `${FOLDER}/spec.md`), /spec\.md/);
  assert.equal(git(dir, 'diff', 'HEAD', '--', `${FOLDER}/spec.md`), '');
  expectState(dir, {
    row: 6,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 7: none accepted, with a task built', () => {
  const dir = repo();
  draft(dir);
  writeFileSync(join(dir, FOLDER, 'build', 'app.md'), BUILD_LOG);
  commit(dir, `docs(demo): record T1 ${CHANGE}`, ['build/app.md']);
  expectState(dir, {
    row: 7,
    facts: {
      intent: doc(true, 'draft', 'draft'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: true,
    },
  });
});

test('row 7: a leading run with reopen fix last', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  accept(dir, 'spec');
  write(dir, DOCS.plan, plan('draft') + '\nReworked.\n');
  commit(dir, `docs(demo): reopen fix ${CHANGE}`, [DOCS.plan]);
  expectState(dir, {
    row: 7,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'accepted', 'accepted'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'leading',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 7: a leading run with draft fix last', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  write(dir, DOCS.spec, spec('draft') + '\nRedrafted.\n');
  commit(dir, `docs(demo): draft fix ${CHANGE}`, [DOCS.spec]);
  expectState(dir, {
    row: 7,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'leading',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 7: a build-log commit after the acceptance does not count as the last step', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  writeFileSync(join(dir, FOLDER, 'build', 'app.md'), BUILD_LOG);
  commit(dir, `docs(demo): record T1 ${CHANGE}`, ['build/app.md']);
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.facts.acceptanceLast, true);
  assert.equal(out.row, 5);
});

test('row 8: all three accepted, nothing uncommitted', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  accept(dir, 'spec');
  accept(dir, 'plan');
  expectState(dir, {
    row: 8,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'accepted', 'accepted'),
      plan: doc(true, 'accepted', 'accepted'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'all',
      acceptanceLast: true,
      built: false,
    },
  });
});

test('row 8: an uncommitted build log and review stay row 8, and neither counts as built', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  accept(dir, 'spec');
  accept(dir, 'plan');
  writeFileSync(join(dir, FOLDER, 'build', 'app.md'), BUILD_LOG);
  writeFileSync(join(dir, FOLDER, 'build', 'review.md'), BUILD_LOG);
  const expected = {
    row: 8,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'accepted', 'accepted'),
      plan: doc(true, 'accepted', 'accepted'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'all',
      acceptanceLast: true,
      built: false,
    },
  };
  expectState(dir, expected);
  commit(dir, `docs(demo): review ${CHANGE}`, ['build/review.md']);
  expectState(dir, expected);
  commit(dir, `docs(demo): record T1 ${CHANGE}`, ['build/app.md']);
  expectState(dir, { ...expected, facts: { ...expected.facts, built: true } });
});

test('row 8: an accepted fix whose plan in HEAD has a newer layout reads as not built', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  accept(dir, 'spec');
  write(dir, DOCS.plan, plan('accepted', '2222222222222222222222222222222222222222', { layout: 99 }));
  commit(dir, `docs(demo): accept plan ${CHANGE}`, [DOCS.plan]);
  writeFileSync(join(dir, FOLDER, 'build', 'app.md'), BUILD_LOG);
  commit(dir, `docs(demo): record T1 ${CHANGE}`, ['build/app.md']);
  expectState(dir, {
    row: 8,
    facts: {
      intent: doc(true, 'accepted', 'accepted'),
      spec: doc(true, 'accepted', 'accepted'),
      plan: doc(true, 'accepted', 'accepted'),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'all',
      acceptanceLast: true,
      built: false,
    },
  });
});

test('row 8: a build log in HEAD the reader cannot read counts as not built', () => {
  const dir = repo();
  draft(dir);
  accept(dir, 'intent');
  accept(dir, 'spec');
  accept(dir, 'plan');
  writeFileSync(join(dir, FOLDER, 'build', 'app.md'), '```jsonc\n{ "layout": [2 }\n```\n\n### T1\n');
  commit(dir, `docs(demo): record T1 ${CHANGE}`, ['build/app.md']);
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.row, 8);
  assert.equal(out.facts.built, false);
});

test('row 1: a rejected fix whose plan in HEAD has broken front matter', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.plan, '```jsonc\n{ "layout": 2, "status": [ }\n```\n\n# Plan\n');
  writeFileSync(join(dir, FOLDER, 'build', 'app.md'), BUILD_LOG);
  commit(dir, `docs(demo): draft fix ${CHANGE}`, [DOCS.plan, 'build/app.md']);
  write(dir, DOCS.intent, intent('rejected'));
  commit(dir, `docs(demo): reject intent ${CHANGE}`, [DOCS.intent]);
  expectState(dir, {
    row: 1,
    facts: {
      intent: doc(true, 'rejected', 'rejected'),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, null, null),
      unrecorded: [],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 2: an unreadable HEAD status with a build log in HEAD', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.plan, '```jsonc\n{ "layout": 2, "status": [ }\n```\n\n# Plan\n');
  writeFileSync(join(dir, FOLDER, 'build', 'app.md'), BUILD_LOG);
  commit(dir, `docs(demo): draft fix ${CHANGE}`, [DOCS.plan, 'build/app.md']);
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(result.stdout).row, 2);
});

test('row 3: a deleted working intent.md takes the track from HEAD', () => {
  const dir = repo();
  draft(dir);
  rmSync(join(dir, FOLDER, DOCS.intent));
  expectState(dir, {
    row: 3,
    facts: {
      intent: doc(true, 'draft', null),
      spec: doc(true, 'draft', 'draft'),
      plan: doc(true, 'draft', 'draft'),
      unrecorded: ['intent'],
      ownLinesOnly: false,
      accepted: 'none',
      acceptanceLast: false,
      built: false,
    },
  });
});

test('row 3: a working intent.md edited to name no track, or with broken front matter, takes the track from HEAD', () => {
  for (const text of [intent('draft', { track: null }), '```jsonc\n{ "track": [ }\n```\n\n# Intent\n']) {
    const dir = repo();
    draft(dir);
    write(dir, DOCS.intent, text);
    const result = fixState(dir);
    assert.equal(result.status, 0, result.stderr);
    const out = JSON.parse(result.stdout);
    assert.equal(out.row, 3);
    assert.deepEqual(out.facts.unrecorded, ['intent']);
  }
});

test('several folders print one entry per folder in the order given, an error included', () => {
  const awaiting = repo();
  draft(awaiting);
  const accepted = repo();
  draft(accepted);
  accept(accepted, 'intent');
  accept(accepted, 'spec');
  accept(accepted, 'plan');
  const notAFix = repo();
  write(notAFix, DOCS.intent, intent('draft', { track: 'full' }));
  commit(notAFix, `docs(demo): draft intent ${CHANGE}`, [DOCS.intent]);

  const folders = [join(accepted, FOLDER), join(notAFix, FOLDER), join(awaiting, FOLDER)];
  const result = spawnSync(dotnet, [program, 'fix-state', '--json', ...folders], {
    cwd: awaiting,
    env,
    encoding: 'utf8',
  });
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(Array.isArray(out), true);
  assert.deepEqual(out.map((e) => e.folder), folders);
  assert.equal(out[0].result.row, 8);
  assert.equal('error' in out[0], false);
  assert.equal('result' in out[1], false);
  assert.match(out[1].error, /track/);
  assert.equal(out[2].result.row, 6);
  assert.deepEqual(out[2].result, JSON.parse(fixState(awaiting).stdout));
});

test('exit 2 when fix-state is given no folder', () => {
  const result = spawnSync(dotnet, [program, 'fix-state', '--json'], { encoding: 'utf8', env });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /usage/);
});

test('exit 2 when the folder has no intent.md', () => {
  const dir = repo();
  write(dir, DOCS.spec, spec('draft'));
  const result = fixState(dir);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /intent\.md/);
  assert.equal(result.stdout, '');
});

test('exit 2 when the intent is not on the fix track', () => {
  for (const track of ['full', null]) {
    const dir = repo();
    write(dir, DOCS.intent, intent('draft', { track }));
    commit(dir, `docs(demo): draft intent ${CHANGE}`, [DOCS.intent]);
    const result = fixState(dir);
    assert.equal(result.status, 2, `track ${track}`);
    assert.match(result.stderr, /track/);
  }
});

test('exit 2 when the folder is not in a git repository', () => {
  const dir = tempDir('hoshin-fix-state-');
  mkdirSync(join(dir, FOLDER), { recursive: true });
  write(dir, DOCS.intent, intent('draft'));
  const result = spawnSync(dotnet, [program, 'fix-state', '--json', join(dir, FOLDER)], {
    cwd: dir,
    env: { ...env, GIT_CEILING_DIRECTORIES: dirname(dir) },
    encoding: 'utf8',
  });
  assert.equal(result.status, 2);
  assert.notEqual(result.stderr, '');
});

function script(dir, name, body) {
  const path = join(dir, '.git', name);
  writeFileSync(path, `#!/bin/sh\n${body}\n`, { mode: 0o755 });
  return path.split(String.fromCharCode(92)).join('/');
}

test('row 4: a diff.external that prints something else, or fails, does not change an unfinished acceptance', () => {
  for (const body of ['echo something else entirely', 'exit 7']) {
    const dir = repo();
    draft(dir);
    write(dir, DOCS.intent, intent('accepted'));
    git(dir, 'config', 'diff.external', script(dir, 'external.sh', body));
    const result = fixState(dir);
    assert.equal(result.status, 0, `${body}: ${result.stderr}`);
    const out = JSON.parse(result.stdout);
    assert.equal(out.row, 4, body);
    assert.equal(out.facts.ownLinesOnly, true, body);
  }
});

test('row 3: a textconv filter on markdown does not hide an edit', () => {
  const dir = repo();
  draft(dir);
  git(dir, 'config', 'diff.hide.textconv', script(dir, 'textconv.sh', 'echo the same for every version'));
  writeFileSync(join(dir, '.git', 'info', 'attributes'), '*.md diff=hide\n');
  write(dir, DOCS.intent, intent('draft').replace('"Hello"', '"Hi"'));
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.row, 3);
  assert.deepEqual(out.facts.unrecorded, ['intent']);
});

test('row 4: a textconv filter on markdown does not hide the lines an acceptance wrote', () => {
  const dir = repo();
  draft(dir);
  git(dir, 'config', 'diff.hide.textconv', script(dir, 'textconv.sh', 'echo the same for every version'));
  writeFileSync(join(dir, '.git', 'info', 'attributes'), '*.md diff=hide\n');
  write(dir, DOCS.intent, intent('accepted'));
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(result.stdout).row, 4);
});

test('row 1: a failing diff.external does not hide a rejected fix', () => {
  const dir = repo();
  draft(dir);
  write(dir, DOCS.intent, intent('rejected'));
  commit(dir, `docs(demo): reject intent ${CHANGE}`, [DOCS.intent]);
  write(dir, DOCS.spec, spec('draft').replace('"Hello"', '"Hi"'));
  git(dir, 'config', 'diff.external', script(dir, 'external.sh', 'exit 7'));
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(result.stdout).row, 1);
});

test('row 4: a byte order mark in both copies still reads an unfinished acceptance', () => {
  const dir = repo();
  write(dir, DOCS.intent, '\uFEFF' + intent('draft'));
  write(dir, DOCS.spec, spec('draft'));
  write(dir, DOCS.plan, plan('draft'));
  commit(dir, `docs(demo): draft fix ${CHANGE}`, Object.values(DOCS));
  write(dir, DOCS.intent, '\uFEFF' + intent('accepted'));
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.row, 4);
  assert.deepEqual(out.facts.unrecorded, []);
});

test('row 4: a HEAD copy with mixed line endings and correct own lines', () => {
  const dir = repo();
  const mixed = intent('draft').replace('# Intent: Fix the greeting typo\n', '# Intent: Fix the greeting typo\r\n');
  write(dir, DOCS.intent, mixed);
  write(dir, DOCS.spec, spec('draft'));
  write(dir, DOCS.plan, plan('draft'));
  commit(dir, `docs(demo): draft fix ${CHANGE}`, Object.values(DOCS));
  write(dir, DOCS.intent, mixed.replace('"status": "draft"', '"status": "accepted"'));
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.row, 4);
  assert.deepEqual(out.facts.unrecorded, []);
});

test('row 4: a HEAD copy with mixed line endings inside the front matter, own lines ending as the lines they replace', () => {
  const dir = repo();
  const mixed = intent('draft').replace('"status": "draft",\n', '"status": "draft",\r\n');
  write(dir, DOCS.intent, mixed);
  write(dir, DOCS.spec, spec('draft'));
  write(dir, DOCS.plan, plan('draft'));
  commit(dir, `docs(demo): draft fix ${CHANGE}`, Object.values(DOCS));
  write(dir, DOCS.intent, mixed.replace('"status": "draft",\r\n', '"status": "accepted",\r\n'));
  const result = fixState(dir);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(result.stdout).row, 4);
});

test('a batch folder that does not exist is reported as missing, not as git failing to start', () => {
  const dir = repo();
  draft(dir);
  const missing = join(dir, 'intent', 'demo', 'no-such-change');
  const result = spawnSync(dotnet, [program, 'fix-state', '--json', missing, join(dir, FOLDER)], {
    cwd: dir,
    env,
    encoding: 'utf8',
  });
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.match(out[0].error, /does not exist/);
  assert.doesNotMatch(out[0].error, /ENOENT|spawnSync/);
  assert.equal(out[1].result.row, 6);
});

test('a batch folder that is a file is its own error entry and the other folders keep their results', () => {
  const dir = repo();
  draft(dir);
  const file = join(dir, FOLDER, 'intent.md');
  const result = spawnSync(dotnet, [program, 'fix-state', '--json', file, join(dir, FOLDER)], {
    cwd: dir,
    env,
    encoding: 'utf8',
  });
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(typeof out[0].error, 'string');
  assert.equal(out[1].result.row, 6);
});

test('exit 2 when the single folder does not exist, naming it', () => {
  const dir = repo();
  const missing = join(dir, 'intent', 'demo', 'no-such-change');
  const result = fixState(dir, missing);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /does not exist/);
});

test('acceptanceLast follows git when the newest commit touching the documents is a merge', () => {
  const dir = repo();
  draft(dir);
  const base = git(dir, 'rev-parse', '--abbrev-ref', 'HEAD').trim();
  git(dir, 'checkout', '-q', '-b', 'side');
  accept(dir, 'intent');
  git(dir, 'checkout', '-q', base);
  writeFileSync(join(dir, 'other.txt'), 'x\n');
  git(dir, 'add', '--', 'other.txt');
  git(dir, 'commit', '-q', '-m', 'chore: unrelated', '--', 'other.txt');
  git(dir, 'merge', '--no-ff', 'side', '-m', 'Merge branch side');
  const paths = Object.values(DOCS).map((n) => `${FOLDER}/${n}`);
  const subject = git(dir, 'log', '-1', '--format=%s', '--', ...paths).trim();
  const result = JSON.parse(fixState(dir).stdout);
  assert.equal(result.facts.acceptanceLast, /accept (intent|spec|plan) /.test(subject) && subject.endsWith(CHANGE));
});
