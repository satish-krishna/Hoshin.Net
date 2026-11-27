// Self-test for setup-sets-team-naming: each grader is tried on what a passing run leaves (the second pattern
// accepted, stored and committed as one setup commit) and on what a failing run leaves (the refused pattern kept, two
// commits, files other than the marker written). A passing run's marker is checked by the program's naming command.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bash, fixture, matches, skill, target, traceLine, withHelpers } from './lib.mjs';

const CASE = 'setup-sets-team-naming';
let ws;
const built = () => (ws ??= fixture(CASE));

const BRANCH = '{type}/{scope}-{slug}';
const TITLE = '{type}({scope}): {title}';

const setNaming = (dir, naming) => {
  const path = join(dir, 'master', '.hoshin.json');
  const marker = JSON.parse(readFileSync(path, 'utf8'));
  marker.systems.demo.naming = naming;
  writeFileSync(path, JSON.stringify(marker, null, 2) + '\n');
};

test(`${CASE}: the skill grader sees setup fire, plain or namespaced, and nothing else`, () => {
  assert.ok(matches(CASE, 'skill-fired', skill('hoshin:setup')));
  assert.ok(matches(CASE, 'skill-fired', skill('setup')));
  assert.ok(!matches(CASE, 'skill-fired', skill('hoshin:fix')));
});

test(`${CASE}: the trace grader fails on the fixture as built and passes once a hook has written the trace`, () => {
  const dir = built();
  assert.ok(!matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')), 'a fixture with no run must have no trace');
  mkdirSync(join(dir, 'master', '.git', 'hoshin'), { recursive: true });
  writeFileSync(join(dir, 'master', '.git', 'hoshin', 'hooks.jsonl'), traceLine());
  assert.ok(matches(CASE, 'hook-trace-has-an-entry', target(dir, CASE, 'hook-trace-has-an-entry')));
});

test(`${CASE}: no-hand-commit fires on a typed git commit and not on other git commands`, () => {
  assert.ok(matches(CASE, 'no-hand-commit', bash('git -C master commit -am "chore(demo): set up hoshin for demo"')));
  assert.ok(!matches(CASE, 'no-hand-commit', bash('git -C master log --oneline')));
});

test(`${CASE}: as built, there is no naming setting and no setup commit, and the marker keeps its repository and hook`, () => {
  const dir = built();
  for (const grader of ['one-setup-commit-landed', 'marker-holds-the-branch-pattern', 'marker-holds-the-title-pattern']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} passes before the run`);
  }
  for (const grader of ['marker-keeps-the-rest']) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on the fixture as built`);
  }
  for (const grader of ['not-two-setup-commits', 'no-other-commit-landed', 'refused-pattern-not-kept']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} fires on the fixture as built`);
  }
});

test(`${CASE}: the program accepts the second branch pattern and refuses the first, which has a space`, () => {
  const dir = built();
  setNaming(dir, { branch: '{type}/{scope} {slug}', pullRequestTitle: TITLE });
  const refused = withHelpers(dir, 'cd master && hoshin naming --json .hoshin.json demo || true');
  assert.match(refused, /"problems":\[".+"\]/, 'the first pattern has a problem');
  assert.ok(matches(CASE, 'refused-pattern-not-kept', target(dir, CASE, 'refused-pattern-not-kept')), 'a refused pattern left in the marker is seen');
  setNaming(dir, { branch: BRANCH, pullRequestTitle: TITLE });
  const accepted = withHelpers(dir, 'cd master && hoshin naming --json .hoshin.json demo');
  assert.match(accepted, /"problems":\[\]/, accepted);
});

test(`${CASE}: what the program leaves when the checked marker is committed as the setup commit satisfies every grader`, () => {
  const dir = built();
  const out = withHelpers(dir, 'cd master && hoshin commit --json --kind setup --system demo -- .hoshin.json');
  assert.match(out, /"committed":true/, out);
  for (const grader of ['one-setup-commit-landed', 'marker-holds-the-branch-pattern', 'marker-holds-the-title-pattern', 'marker-keeps-the-rest']) {
    assert.ok(matches(CASE, grader, target(dir, CASE, grader)), `${grader} fails on a passing run`);
  }
  for (const grader of ['not-two-setup-commits', 'no-other-commit-landed', 'refused-pattern-not-kept']) {
    assert.ok(!matches(CASE, grader, target(dir, CASE, grader)), `${grader} fires on a passing run`);
  }
});

test(`${CASE}: a second setup commit or a commit with another subject is seen`, () => {
  const dir = built();
  setNaming(dir, { branch: '{type}/{scope}-{slug}-2', pullRequestTitle: TITLE });
  assert.match(withHelpers(dir, 'cd master && hoshin commit --json --kind setup --system demo -- .hoshin.json'), /"committed":true/);
  assert.ok(matches(CASE, 'not-two-setup-commits', target(dir, CASE, 'not-two-setup-commits')), 'two setup commits');
  withHelpers(dir, 'cd master && echo x > stray.txt && git_in add stray.txt && git_in commit -q -m "docs(demo): other"');
  assert.ok(matches(CASE, 'no-other-commit-landed', target(dir, CASE, 'no-other-commit-landed')), 'a commit with another subject');
});

test(`${CASE}: the marker graders fail on a marker with one pattern, the wrong pattern, or the rest of the marker gone`, () => {
  const marker = (naming, rest = ', "hooks": { "build.finish": [{ "id": "unit", "run": ["bash", "test.sh"] }] }') =>
    `{ "version": 1, "systems": { "demo": { "repos": ["app"]${naming ? `, "naming": ${naming}` : ''} } }${rest} }`;
  const both = marker('{ "branch": "{type}/{scope}-{slug}", "pullRequestTitle": "{type}({scope}): {title}" }');
  assert.ok(matches(CASE, 'marker-holds-the-branch-pattern', both));
  assert.ok(matches(CASE, 'marker-holds-the-title-pattern', both));
  assert.ok(matches(CASE, 'marker-keeps-the-rest', both));
  assert.ok(!matches(CASE, 'marker-holds-the-branch-pattern', marker('{ "pullRequestTitle": "{type}({scope}): {title}" }')), 'no branch pattern');
  assert.ok(!matches(CASE, 'marker-holds-the-title-pattern', marker('{ "branch": "{type}/{scope}-{slug}" }')), 'no title pattern');
  assert.ok(!matches(CASE, 'marker-holds-the-branch-pattern', marker('{ "branch": "{type}/{slug}", "pullRequestTitle": "{title}" }')), 'another branch pattern');
  assert.ok(!matches(CASE, 'marker-holds-the-title-pattern', marker('{ "branch": "{type}/{scope}-{slug}", "pullRequestTitle": "{title}" }')), 'another title pattern');
  assert.ok(!matches(CASE, 'marker-holds-the-branch-pattern', both.replace('"naming"', '"labels"')), 'the patterns are not in the naming setting');
  assert.ok(matches(CASE, 'refused-pattern-not-kept', marker('{ "branch": "{type}/{scope} {slug}", "pullRequestTitle": "{title}" }')), 'the refused pattern was kept');
  assert.ok(!matches(CASE, 'marker-keeps-the-rest', marker('{ "branch": "{type}/{scope}-{slug}", "pullRequestTitle": "{title}" }', '')), 'the hook was dropped');
});

test(`${CASE}: the other-file graders pin what their paths depend on, and the report graders want the patterns and when they take effect`, () => {
  const dir = built();
  // file_exists is judged by the runner; this pins that writing the instructions creates the two paths the graders watch.
  withHelpers(dir, 'cd master && hoshin instructions --json --write');
  assert.match(withHelpers(dir, 'ls master/intent/workspace.md CLAUDE.md'), /CLAUDE\.md/);
  assert.ok(matches(CASE, 'report-names-the-patterns', `Set the branch pattern to ${BRANCH} and the title pattern to ${TITLE}. The first pattern was refused.`));
  assert.ok(!matches(CASE, 'report-names-the-patterns', `Set the branch pattern to ${BRANCH}.`), 'one pattern is not both');
  assert.ok(!matches(CASE, 'report-names-the-patterns', 'Set the naming.'));
  assert.ok(matches(CASE, 'report-says-when-it-takes-effect', 'The other engineers get the setting once it is on the base branch.'));
  assert.ok(matches(CASE, 'report-says-when-it-takes-effect', 'It applies once this commit is merged.'));
  assert.ok(!matches(CASE, 'report-says-when-it-takes-effect', 'Done.'));
});
