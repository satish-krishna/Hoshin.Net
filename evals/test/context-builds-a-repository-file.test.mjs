// Self-test for context-builds-a-repository-file: each grader is tried on what a passing run leaves and on what a failing run leaves.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, graders, matches, read, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'context-builds-a-repository-file';
const FILE = 'master/intent/demo/context/app.md';
const on = (ws, grader) => matches(CASE, grader, target(ws, CASE, grader));
const g = (name, text) => matches(CASE, name, text);

// What a passing run writes: the app's own HEAD, the clear rules seen in code, and the disagreement as intent unknown.
const goodFile = (sha) => `\`\`\`jsonc
{ "repo": "app", "system": "demo", "source_commit": "${sha}", "built": "2026-11-26", "built_by": "Eve Engineer" }
\`\`\`

# Context: app

The app greets visitors when they open the site and tells shoppers what shipping costs.

## Domain terms

| Term | Meaning in this business |
|---|---|
| returning visitor | someone who has been here before |

## What users can do today

- **Visitor:** be greeted by name when they come back.
- **Shopper:** see a shipping estimate in the cart and be charged shipping at checkout.

## Rules the system enforces

| # | Rule | Provenance | Note |
|---|---|---|---|
| C1 | A returning visitor who gave a first name sees "Welcome back, " followed by that name | seen in code | |
| C2 | A returning visitor who gave no name sees "Welcome back" | seen in code | |
| C3 | A first-time visitor sees no greeting | seen in code | |
| C4 | Shipping is free from 50.00 in the cart but from 42.18 at checkout, so a shopper can be told one thing and charged another | intent unknown | The cart and checkout disagree; product should say which threshold is wanted (cart.sh, checkout.sh) |

## Known oddities

None.
`;

const sha = () => /"source_commit"\\s\*:\\s\*"([0-9a-f]{40})"/.exec(graders(CASE)['source-commit-is-the-apps-head'].pattern)[1];

test(`${CASE}: the skill grader sees context, plain or namespaced`, () => {
  assert.ok(g('skill-fired', skill('hoshin:context')));
  assert.ok(g('skill-fired', skill('context', 'demo')));
  assert.ok(!g('skill-fired', skill('hoshin:setup')));
});

test(`${CASE}: the fixture has an app with the pinned head, a master with no context file, and a trace grader that fails until a hook has run`, () => {
  const ws = fixture(CASE);
  assert.equal(withHelpers(ws, 'git -C app rev-parse HEAD').trim(), sha(), 'the pinned source_commit is the fixture\'s app HEAD, on every machine');
  assert.equal(read(ws, FILE), '', 'no context file to start from');
  assert.ok(on(ws, 'product-repository-unchanged'));
  assert.ok(!on(ws, 'committed-once-by-the-library'), 'no context commit in the fixture');
  assert.ok(!on(ws, 'hook-trace-has-an-entry'));
  mkdirSync(join(ws, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(ws, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(on(ws, 'hook-trace-has-an-entry'));
});

test(`${CASE}: a file committed through the program passes the reflog grader once, and a second commit or a hand commit fails it`, () => {
  const ws = fixture(CASE);
  mkdirSync(join(ws, 'master', 'intent', 'demo', 'context'), { recursive: true });
  writeFileSync(join(ws, FILE), goodFile(sha()));
  withHelpers(ws, `cd master && hoshin commit --json --kind context --system demo -- intent/demo/context/app.md | grep -q '"committed":true'`);
  assert.ok(on(ws, 'committed-once-by-the-library'), 'build context for app, once');
  withHelpers(ws, 'cd master && printf "\\nA later edit.\\n" >> intent/demo/context/app.md && hoshin commit --json --kind context --system demo -- intent/demo/context/app.md | grep -q \'"committed":true\'');
  assert.ok(!on(ws, 'committed-once-by-the-library'), 'a second commit, now a refresh');
  const hand = fixture(CASE);
  withHelpers(hand, 'cd master && mkdir -p intent/demo/context && echo x > intent/demo/context/app.md && git_in add . && git_in commit -q -m "docs(demo): add context"');
  assert.ok(!on(hand, 'committed-once-by-the-library'), 'a hand commit has no library subject');
});

test(`${CASE}: the file graders pass the good file`, () => {
  const text = goodFile(sha());
  for (const grader of ['front-matter-has-the-fields', 'source-commit-is-the-apps-head', 'rules-are-numbered-from-c1', 'greeting-rules-are-seen-in-code', 'shipping-disagreement-is-intent-unknown']) {
    assert.ok(g(grader, text), grader);
  }
  for (const grader of ['every-rule-has-a-provenance-tag', 'nothing-confirmed-without-asking', 'rules-and-prose-name-no-files']) {
    assert.ok(!g(grader, text), `${grader} is a not_contains grader: its pattern must not match the good file`);
  }
});

test(`${CASE}: the front matter and source_commit graders fail a file with a missing field or another commit`, () => {
  const text = goodFile(sha());
  assert.ok(!g('front-matter-has-the-fields', text.replace(', "built_by": "Eve Engineer"', '')), 'no built_by');
  assert.ok(!g('front-matter-has-the-fields', text.replace('"built_by": "Eve Engineer"', '"built_by": ""')), 'an empty built_by');
  assert.ok(!g('front-matter-has-the-fields', text.replace('"repo": "app", ', '')), 'no repo');
  assert.ok(!g('front-matter-has-the-fields', text.replace('"system": "demo"', '"system": "shop"')), 'the wrong system');
  assert.ok(!g('front-matter-has-the-fields', text.replace('"2026-11-26"', '"today"')), 'a built that is not a date');
  assert.ok(!g('front-matter-has-the-fields', text.replace('```jsonc', '---').replace('```\n', '---\n')), 'YAML front matter');
  assert.ok(!g('source-commit-is-the-apps-head', text.replace(sha(), 'a'.repeat(40))), 'an invented commit');
  const ws = fixture(CASE);
  const masterHead = withHelpers(ws, 'git -C master rev-parse HEAD').trim();
  assert.ok(!g('source-commit-is-the-apps-head', text.replace(sha(), masterHead)), 'the master\'s own HEAD, the usual mistake');
  assert.ok(!g('source-commit-is-the-apps-head', text.replace(sha(), sha().slice(0, 7))), 'an abbreviated commit');
});

test(`${CASE}: the rules graders fail a missing rules section, an untagged rule and a wrong tag`, () => {
  const text = goodFile(sha());
  assert.ok(!g('rules-are-numbered-from-c1', text.replace('| C1 |', '| 1 |')), 'rules not numbered C1');
  assert.ok(!g('rules-are-numbered-from-c1', text.replace('## Rules the system enforces', '## Rules')), 'a renamed section');
  assert.ok(g('every-rule-has-a-provenance-tag', text.replace('| seen in code | |\n| C2', '| | |\n| C2')), 'an empty tag');
  assert.ok(g('every-rule-has-a-provenance-tag', text.replace('| seen in code | |\n| C2', '| probably | |\n| C2')), 'a made-up tag');
  assert.ok(!g('greeting-rules-are-seen-in-code', text.replaceAll('| seen in code |', '| intent unknown |')), 'clear rules left as unknown');
  assert.ok(!g('shipping-disagreement-is-intent-unknown', text.replace('| intent unknown |', '| seen in code |')), 'a contradiction taken for clear');
  assert.ok(!g('shipping-disagreement-is-intent-unknown', text.replace('| intent unknown |', '| confirmed by engineer |')), 'a contradiction confirmed with nobody asked');
  assert.ok(!g('shipping-disagreement-is-intent-unknown', text.replace(/^\| C4 .*\n/m, '')), 'the disagreement left out');
});

test(`${CASE}: the engineer is away, so a confirmed tag on a rule or an oddity fires the not_contains grader`, () => {
  const text = goodFile(sha());
  assert.ok(g('nothing-confirmed-without-asking', text.replaceAll('| seen in code |', '| confirmed by engineer |')), 'a rule confirmed');
  assert.ok(g('nothing-confirmed-without-asking', text.replace('None.', '| Oddity | Provenance | Note |\n|---|---|---|\n| The cart and checkout disagree | confirmed by engineer | a bug |')), 'an oddity confirmed');
  assert.ok(!g('nothing-confirmed-without-asking', text.replace('## Known oddities', '## Provenance tags\n\n| Tag | Meaning |\n|---|---|\n| confirmed by engineer | asked and confirmed |\n\n## Known oddities')), 'a legend of the tags is not a tagged rule');
});

test(`${CASE}: files named in a rule or in prose fire the not_contains grader, and a Note may name them`, () => {
  const text = goodFile(sha());
  assert.ok(!g('rules-and-prose-name-no-files', text), 'the good file names cart.sh and checkout.sh in a Note only');
  assert.ok(g('rules-and-prose-name-no-files', text.replace('A first-time visitor sees no greeting', 'greet.sh prints nothing for a first-time visitor')), 'a rule that names a file');
  assert.ok(g('rules-and-prose-name-no-files', text.replace('tells shoppers what shipping costs.', 'runs cart.sh and checkout.sh.')), 'prose that names a file');
  assert.ok(!g('rules-and-prose-name-no-files', text.replace('tells shoppers what shipping costs.', 'tells shoppers what shipping costs. It is a shell script.')));
});

test(`${CASE}: the report grader needs the shipping doubt, with the two thresholds or the word for the disagreement`, () => {
  for (const ok of ['Wrote the context for app: 4 rules, 1 intent unknown. Shipping is free from 50.00 in the cart but from 42.18 at checkout; I could not ask which is right.', 'The shipping thresholds in the cart and checkout disagree, so I tagged that rule intent unknown.', 'One open question: free shipping is 5000 cents in one place and 4217 in the other.']) {
    assert.ok(g('report-names-the-shipping-doubt', ok), ok);
  }
  for (const no of ['Wrote the context for app: 4 rules, all seen in code.', 'Wrote the context. One rule is intent unknown.', 'The greeting rules disagree with nothing.']) {
    assert.ok(!g('report-names-the-shipping-doubt', no), no);
  }
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(g('no-hand-commit', bash('git -C master commit -m "docs(demo): build context for app"')));
  assert.ok(!g('no-hand-commit', bash('git -C app rev-parse HEAD')));
});
