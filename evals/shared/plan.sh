#!/usr/bin/env bash
# Helpers for the plan cases and the intent-scope cases (evals/README.md, B03 rows 28 to 38). Source ../shared/workspace.sh
# first; this file only defines functions.
#
# A plan case starts from a change whose intent and spec are accepted with real pins. The documents are written by hand
# because the model's work is not what is under test, but every commit and every pin goes through the program: the
# program's `commit --kind` makes the subjects and runs the checks, and a pin is the last commit of the upstream
# document, which is what the engine itself pins.

PLAN_CHANGE="${PLAN_CHANGE:-2026-11-01-greeting}"

# Sets the base workspace's marker to the one the plan cases want (no build.finish hooks, so the plan has to name the
# whole-app checks itself) and commits it as a setup commit.
plan_marker_without_hooks() {
  (
    cd master
    cat > .hoshin.json <<'JSON'
{ "version": 1, "systems": { "demo": { "repos": ["app"] } } }
JSON
    hoshin commit --json --kind setup --system demo -- .hoshin.json >/dev/null
  )
}

# Replaces the base workspace's shell app with a small web app, then refreshes the context file to match it. The variant
# is "contributing" (the contributing guide asks for a production build and a three-minute browser check before a merge)
# or "rules" (CLAUDE.md links to a page with two rules the code does not show, and a style rule that is the builder's).
plan_app_web() {
  local variant="$1"
  (
    cd app
    git_in rm -q greet.sh test.sh
    mkdir -p src
    cat > package.json <<'JSON'
{
  "name": "app",
  "private": true,
  "type": "module",
  "scripts": {
    "test": "vitest run",
    "lint": "eslint src"
  }
}
JSON
    cat > src/home.ts <<'TS'
export function homeHeader(visitor: { returning: boolean; firstName?: string }): string {
  return 'Welcome';
}
TS
    cat > src/home.test.ts <<'TS'
import { expect, test } from 'vitest';
import { homeHeader } from './home';

test('the header welcomes everyone', () => {
  expect(homeHeader({ returning: false })).toBe('Welcome');
});
TS
    case "$variant" in
      contributing)
        mkdir -p scripts
        cat > package.json <<'JSON'
{
  "name": "app",
  "private": true,
  "type": "module",
  "scripts": {
    "test": "vitest run",
    "lint": "eslint src",
    "build": "vite build",
    "check:boot": "node scripts/check-boot.mjs"
  }
}
JSON
        cat > CONTRIBUTING.md <<'MD'
# Contributing

- Unit tests live next to the code as `*.test.ts`. Run one file with `npm test -- src/<file>.test.ts`; `npm test` runs them all.
- `npm run lint` checks the code style.
- Before merging, `npm run build` must pass (a production build, about 90 seconds) and `npm run check:boot` must pass: it builds the app and opens every route in a headless browser, which takes about three minutes.
MD
        cat > scripts/check-boot.mjs <<'JS'
// Builds the app and opens every route in a headless browser; fails when a route throws or renders blank.
console.log('check:boot: every route rendered');
JS
        ;;
      rules)
        mkdir -p src/copy docs
        cat > CLAUDE.md <<'MD'
# app

- Unit tests live next to the code as `*.test.ts`. Run one file with `npm test -- src/<file>.test.ts`.
- Every exported function has a doc comment.
- Before changing anything a visitor sees, read [docs/visible-changes.md](docs/visible-changes.md).
MD
        cat > docs/visible-changes.md <<'MD'
# Changes a visitor sees

- Text a visitor can see lives in `src/copy/en.json`, keyed by screen, and code reads it with `copy('<key>')` from `src/copy/index.ts`. Never put visible text in a component. `src/home.ts` still holds a literal from before this rule; new text goes in the copy file.
- Every change a visitor can see adds a line under `## Unreleased` in `CHANGELOG.md`, in the same pull request.
MD
        cat > CHANGELOG.md <<'MD'
# Changelog

## Unreleased

## 1.2.0

- The home page has a header.
MD
        cat > src/copy/en.json <<'JSON'
{ "settings.title": "Settings" }
JSON
        cat > src/copy/index.ts <<'TS'
import text from './en.json' with { type: 'json' };

/** Returns the visible text stored under a key. */
export function copy(key: string): string {
  return (text as Record<string, string>)[key] ?? key;
}
TS
        ;;
      *) echo "plan_app_web: unknown variant $variant" >&2; exit 1 ;;
    esac
    git_in add . && git_in commit -q -m "chore: replace the shell app with a web app"
  )
  local head
  head=$(git -C app rev-parse HEAD)
  (
    cd master
    cat > intent/demo/context/app.md <<MD
\`\`\`jsonc
{ "repo": "app", "system": "demo", "source_commit": "$head", "built": "2026-11-26", "built_by": "eval" }
\`\`\`

# Context: app

The app is a web site. Its home page has a header.

## Rules the system enforces

| # | Rule | Provenance | Note |
|---|---|---|---|
| C1 | The home page header reads "Welcome" for every visitor | seen in code | |
MD
    hoshin commit --json --kind context --system demo -- intent/demo/context/app.md >/dev/null
  )
}

# The change's folder inside its copy: plan_folder [change].
plan_folder() { echo "$(copy_of "${1:-$PLAN_CHANGE}")/intent/demo/${1:-$PLAN_CHANGE}"; }

# Writes the accepted-to-be intent. A second argument "outline" adds decision D3 (greetings turned off in settings).
plan_write_intent() {
  local change="$1" variant="${2:-basic}" d3=""
  [ "$variant" = outline ] && d3='| D3 | A returning visitor turned greetings off in their settings | handle it | Show no greeting. |'
  cat > "$(plan_folder "$change")/intent.md" <<MD
\`\`\`jsonc
{ "title": "Greet returning visitors", "system": "demo", "author": "Pat Owner, Product Owner", "date": "2026-11-01", "status": "draft", "scope": "demo" }
\`\`\`

# Intent: Greet returning visitors

## Problem

Returning visitors see the same page as strangers, so they do not feel recognized. Support hears that the site feels cold.

## Proposed outcome

1. A visitor who has been here before sees a short greeting with their first name when they open the site.
2. A first-time visitor sees no greeting.

**Success looks like:** returning visitors see the greeting on every visit, and first-time visitors never do.

## Affected users and systems

- **Users:** returning and first-time visitors.
- **Systems:** the app.

## Constraints

- The greeting uses only the first name the visitor already gave us.

## Decisions

| # | Situation | Decision | What should happen |
|---|---|---|---|
| D1 | A returning visitor never gave a first name | handle it | Show the greeting without a name. |
| D2 | Translating the greeting | out of scope | - |
$d3

## Open questions

- None.
MD
}

# Writes the spec, pinned to the intent's last commit. A second argument "outline" states the greeting as one Scenario
# Outline over three conditions, whose Examples list every reachable combination.
plan_write_spec() {
  local change="$1" variant="${2:-basic}" intent_commit
  intent_commit=$(git -C "$(copy_of "$change")" log -1 --format=%H -- "intent/demo/$change/intent.md")
  local requirements decisions
  if [ "$variant" = outline ]; then
    requirements=$(cat <<'GHERKIN'
Feature: Greet returning visitors
  A returning visitor sees a greeting with their first name.

  @R1 @from:outcome-1 @from:outcome-2 @from:D1 @from:D3
  Rule: The home page greets a visitor by whether they have been here, gave a first name, and turned greetings off
    A first-time visitor has given no first name and has no settings, so those combinations cannot happen.

    @R1.1
    Scenario Outline: What the home page shows
      Given a visitor who <visited> been here before
      And who gave <name>
      And whose greetings are <greetings>
      When the visitor opens the site
      Then the visitor sees <shown>

      Examples:
        | visited | name                | greetings | shown                             |
        | has     | the first name Mina | on        | the greeting "Welcome back, Mina" |
        | has     | no first name       | on        | the greeting "Welcome back"       |
        | has     | the first name Mina | off       | no greeting                       |
        | has     | no first name       | off       | no greeting                       |
        | has not | no first name       | on        | no greeting                       |
GHERKIN
)
    decisions='| D1 | R1.1 |
| D2 | out of scope |
| D3 | R1.1 |'
  else
    requirements=$(cat <<'GHERKIN'
Feature: Greet returning visitors
  A returning visitor sees a greeting with their first name.

  @R1 @from:outcome-1 @from:D1
  Rule: A returning visitor is greeted

    @R1.1
    Scenario: A returning visitor with a first name
      Given a visitor who has been here before and gave the first name Mina
      When the visitor opens the site
      Then the visitor sees a greeting that says Mina

    @R1.2
    Scenario: A returning visitor without a first name
      Given a visitor who has been here before and gave no first name
      When the visitor opens the site
      Then the visitor sees a greeting without a name

  @R2 @from:outcome-2
  Rule: A first-time visitor is not greeted

    @R2.1
    Scenario: A first-time visitor
      Given a visitor who has never been here
      When the visitor opens the site
      Then the visitor sees no greeting
GHERKIN
)
    decisions='| D1 | R1.2 |
| D2 | out of scope |'
  fi
  cat > "$(plan_folder "$change")/spec.md" <<MD
\`\`\`jsonc
{ "layout": 2, "source_intent": "$change/intent.md", "source_intent_commit": "$intent_commit", "title": "Greet returning visitors", "system": "demo", "date": "2026-11-01", "status": "draft", "policies_applied": [], "policies_not_checked": ["security", "compliance", "brand", "UX"] }
\`\`\`

# Spec: Greet returning visitors

## Requirements

\`\`\`gherkin
$requirements
\`\`\`

## Out of scope

- D2: translating the greeting.

## Design

The app shows the greeting at the top of the home page when it recognizes the visitor.

## How each constraint is met

| Constraint (from intent) | Met by |
|---|---|
| The greeting uses only the first name the visitor already gave us | R1.1 |

## Decisions carried from the intent

| Decision | Requirement |
|---|---|
$decisions

## Open questions from the intent

- None.

## Areas of concern

1. **No policy skills were applied.** Owner: product owner. Review the unchecked areas.
MD
}

# Accepts a document the way the engine does: its status line goes from draft to accepted, then the program commits it.
plan_accept() {
  local change="$1" doc="$2"
  sed -i 's/"status": "draft"/"status": "accepted"/' "$(plan_folder "$change")/$doc.md"
  commit_doc "$change" "accept-$doc" "intent/demo/$change/$doc.md"
}

# Starts the change and moves it, through the program, to the state a plan case begins in: intent and spec accepted, both
# pins holding, no plan. plan_change_to_spec [change] [basic|outline]
plan_change_to_spec() {
  local change="${1:-$PLAN_CHANGE}" variant="${2:-basic}"
  change_start "$change" feat demo
  plan_write_intent "$change" "$variant"
  commit_doc "$change" intent-draft "intent/demo/$change/intent.md"
  plan_accept "$change" intent
  plan_write_spec "$change" "$variant"
  commit_doc "$change" spec "intent/demo/$change/spec.md"
  plan_accept "$change" spec
  plan_assert_ready_to_plan "$change"
}

# Refuses the fixture when the program does not see an accepted intent and spec with holding pins and no plan, so a case
# never runs against a state it did not mean to build.
plan_assert_ready_to_plan() {
  local change="$1" out
  out="$(cd "$workspace_root/master" && hoshin state --json --change "$change")"
  case "$out" in
    *'"headStatus":"accepted"'*'"headStatus":"accepted"'*'"pin":"holds"'*) ;;
    *) echo "fixture: $change is not ready to plan: $out" >&2; exit 1 ;;
  esac
  case "$out" in *'"pin":"broken"'*|*'"pin":"missing"'*) echo "fixture: a pin of $change does not hold: $out" >&2; exit 1 ;; esac
}
