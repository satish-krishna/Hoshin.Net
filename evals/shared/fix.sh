#!/usr/bin/env bash
# Shared by the light-track fix cases (group "fix"): the base workspace with the greeting bug in place, and helpers that
# bring a change to a drafted or an accepted fix through the program, so subjects, pins and the change's copy are the
# engine's own. A fixture sources this file, then calls what its case needs.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/workspace.sh"

fix_change=2026-10-24-greeting-space

# Folds whatever the app's working tree holds into its one commit, and points the master's context file at the new head,
# so the context stays current (source_commit = the app's head) and both reflogs stay one commit long.
amend_app() {
  (cd app && git_in add -A && git_in commit -q --amend --no-edit)
  local app_head
  app_head=$(git -C app rev-parse HEAD)
  (
    cd master
    sed -i.bak "s/\"source_commit\": \"[0-9a-f]*\"/\"source_commit\": \"$app_head\"/" intent/demo/context/app.md && rm intent/demo/context/app.md.bak
    grep -q "$app_head" intent/demo/context/app.md
    git_in commit -q -a --amend --no-edit
  )
}

# The bug the fix cases start from: a named visitor is greeted "Welcome back,Mina", which breaks context rule C1. The
# base workspace's app has no such bug.
buggy_greeting() {
  (
    cd app
    sed -i.bak 's/Welcome back${2:+, $2}/Welcome back${2:+,$2}/' greet.sh && rm greet.sh.bak
    grep -q 'back${2:+,$2}' greet.sh
  )
  amend_app
}

# A second repository in the system, registered and given a context file through the program (the setup commit) before
# any change starts, so the change's copy knows it: add_web.
add_web() {
  mkdir web
  (
    cd web
    git init -q -b main
    exclude_sandbox_mounts
    cat > index.html <<'HTML'
<h1>Welcome</h1>
<p id="greeting"></p>
HTML
    git_in add . && git_in commit -q -m "feat: landing page"
  )
  local web_head out
  web_head=$(git -C web rev-parse HEAD)
  (
    cd master
    sed -i.bak 's/"repos": \["app"\]/"repos": ["app", "web"]/' .hoshin.json && rm .hoshin.json.bak
    grep -q '"web"' .hoshin.json
    cat > intent/demo/context/web.md <<MD
\`\`\`jsonc
{ "repo": "web", "system": "demo", "source_commit": "$web_head", "built": "2026-11-26", "built_by": "eval" }
\`\`\`

# Context: web

The web repository serves the site's landing page, which shows the greeting that app prints.

## Rules the system enforces

| # | Rule | Provenance | Note |
|---|---|---|---|
| C1 | The landing page shows the greeting that app prints, unchanged | confirmed by engineer | |
MD
    out="$(hoshin commit --json --kind setup --system demo -- .hoshin.json intent/demo/context/web.md)"
    case "$out" in *'"committed":true'*) ;; *) echo "fixture: registering web refused: $out" >&2; exit 1 ;; esac
  )
}

# The folder of the fix's documents inside the change's copy.
fix_folder() { echo "$(copy_of "${1:-$fix_change}")/intent/demo/${1:-$fix_change}"; }

# Starts the change and commits the drafted fix: draft_fix [change] [two]. The three documents are drafts, track fix,
# the source pins "pending acceptance" as the engine leaves them, committed together as "draft fix". With "two" the fix
# has a second scenario (R1.2, no name) and a second task (T2), which a case that builds T1 and reopens the fix needs.
draft_fix() {
  local change="${1:-$fix_change}"
  local scenario2="" row2="" task2="" app_head
  app_head=$(git -C app rev-parse HEAD)
  local out_of_scope="- The greeting for a returning visitor with no name, and for a first-time visitor."
  local approach="Add the failing test for the named greeting first, then put the space after the comma in greet.sh."
  if [ "${2:-}" = "two" ]; then
    scenario2='
    @R1.2
    Scenario: A returning visitor without a first name
      Given a returning visitor who gave no first name
      When the visitor opens the site
      Then the greeting is "Welcome back."
'
    out_of_scope="- The greeting for a first-time visitor."
    approach="Add the failing test for the named greeting first and put the space after the comma in greet.sh, then give the greeting for a visitor with no name its full stop."
    row2='| R1.2 | a returning visitor with no name is greeted "Welcome back." | T2 |
'
    task2='
### T2: End the greeting without a name with a full stop

```jsonc
{
  "id": "T2",
  "repo": "app",
  "depends_on": ["T1"],
  "uses": [],
  "files": ["greet.sh", "test.sh"],
  "covers": ["R1.2"],
  "supports": []
}
```

#### Change

Change the greeting in greet.sh so a returning visitor who gave no name gets "Welcome back." with a full stop.

#### Tests

Change the no-name check in test.sh to expect "Welcome back.". Run `bash test.sh`. It fails before the change because greet.sh prints "Welcome back" with no full stop.

#### Done when

`bash test.sh` prints ok.
'
  fi
  change_start "$change" fix demo
  local dir
  dir="$(fix_folder "$change")"
  cat > "$dir/intent.md" <<MD
\`\`\`jsonc
{
  "title": "Greeting keeps its space after the comma",
  "system": "demo",
  "author": "eval, engineer",
  "date": "2026-10-24",
  "status": "draft",
  "scope": "demo",
  "track": "fix",
  "kind": "bug",
  "context": ["app@${app_head:0:7}"]
}
\`\`\`

# Intent: Greeting keeps its space after the comma

## Problem

A returning visitor who gave a first name sees "Welcome back," with the name directly after the comma and no space. Context rule C1 says the greeting is "Welcome back, " followed by the name. Every returning visitor with a name sees the typo.

## Root cause

app/greet.sh:4 builds the greeting with \`\${2:+,\$2}\`, which puts the comma and the name together without a space.

## Proposed outcome

A returning visitor who gave a first name sees "Welcome back, " followed by that name.

## Affected users and systems

- **Users:** returning visitors who gave a first name
- **Systems:** app, the greeting script

## Constraints

- The greeting for a returning visitor with no name, and for a first-time visitor, does not change.

## Decisions

| # | Situation | Decision | What should happen |
|---|---|---|---|
| D1 | The name is hyphenated or has several parts | handle it | The whole name follows the comma and the space |

## Open questions

None.
MD
  cat > "$dir/spec.md" <<MD
\`\`\`jsonc
{
  "layout": 2,
  "source_intent": "$change/intent.md",
  "source_intent_commit": "pending acceptance",
  "system": "demo",
  "date": "2026-10-24",
  "status": "draft",
  "policies_applied": [],
  "policies_not_checked": ["security", "compliance", "brand", "UX"]
}
\`\`\`

# Spec: Greeting keeps its space after the comma

## Requirements

\`\`\`gherkin
Feature: Greeting keeps its space after the comma
  A returning visitor who gave a first name sees "Welcome back, " followed by that name.

  @R1 @from:problem
  Rule: A returning visitor with a first name is greeted with a space after the comma

    @R1.1
    Scenario: A returning visitor with a first name
      Given a returning visitor whose first name is Mina
      When the visitor opens the site
      Then the greeting is "Welcome back, Mina"
$scenario2\`\`\`

## Design

The greeting is built in greet.sh. The change puts a space after the comma, which removes the root cause named in the intent.

## Out of scope

$out_of_scope

## Areas of concern

1. **Policy areas not checked.** The light track does not apply policy skills. Owner: the engineer who accepts the fix.
MD
  cat > "$dir/plan.md" <<MD
\`\`\`jsonc
{
  "layout": 2,
  "source_spec": "$change/spec.md",
  "source_spec_commit": "pending acceptance",
  "system": "demo",
  "date": "2026-10-24",
  "author": "eval",
  "status": "draft"
}
\`\`\`

# Plan: Greeting keeps its space after the comma

## Approach

$approach

## Scenario coverage

| Scenario | Test | Task |
|---|---|---|
| R1.1 | a returning visitor named Mina is greeted "Welcome back, Mina" | T1 |
$row2
## Tasks

### T1: Put a space after the comma

\`\`\`jsonc
{
  "id": "T1",
  "repo": "app",
  "depends_on": [],
  "uses": [],
  "files": ["greet.sh", "test.sh"],
  "covers": ["R1.1"],
  "supports": []
}
\`\`\`

#### Change

Change the greeting in greet.sh so a named visitor gets "Welcome back, " followed by the name, which removes the missing space named in the intent.

#### Tests

Add to test.sh a check that \`bash greet.sh returning Mina\` prints "Welcome back, Mina". Run \`bash test.sh\`. It fails before the change because greet.sh prints "Welcome back,Mina".

#### Done when

\`bash test.sh\` prints ok.
$task2
## Risks

- **What could break:** the greeting without a name, which test.sh already protects.
MD
  commit_doc "$change" fix-draft "intent/demo/$change"
}

# Accepts the three documents in order, each in its own commit, each pin naming the commit that holds the document above
# it as accepted, the way a typed accept leaves them: accept_fix [change].
accept_fix() {
  local change="${1:-$fix_change}"
  local dir rel pin
  dir="$(fix_folder "$change")"
  rel="intent/demo/$change"
  sed -i.bak 's/"status": "draft"/"status": "accepted"/' "$dir/intent.md" && rm "$dir/intent.md.bak"
  commit_doc "$change" accept-intent "$rel/intent.md"
  pin=$(git -C "$(copy_of "$change")" log -1 --format=%H -- "$rel/intent.md")
  sed -i.bak -e "s/\"source_intent_commit\": \"[^\"]*\"/\"source_intent_commit\": \"$pin\"/" -e 's/"status": "draft"/"status": "accepted"/' "$dir/spec.md" && rm "$dir/spec.md.bak"
  commit_doc "$change" accept-spec "$rel/spec.md"
  pin=$(git -C "$(copy_of "$change")" log -1 --format=%H -- "$rel/spec.md")
  sed -i.bak -e "s/\"source_spec_commit\": \"[^\"]*\"/\"source_spec_commit\": \"$pin\"/" -e 's/"status": "draft"/"status": "accepted"/' "$dir/plan.md" && rm "$dir/plan.md.bak"
  commit_doc "$change" accept-plan "$rel/plan.md"
}

# The engineer reopens the accepted fix: spec and plan go back to draft in one reopen commit, the intent stays accepted.
# Reopening is typed-only, so a case that starts from it makes the commit here, through the program: reopen_fix [change].
reopen_fix() {
  local change="${1:-$fix_change}"
  local dir
  dir="$(fix_folder "$change")"
  sed -i.bak 's/"status": "accepted"/"status": "draft"/' "$dir/spec.md" && rm "$dir/spec.md.bak"
  sed -i.bak 's/"status": "accepted"/"status": "draft"/' "$dir/plan.md" && rm "$dir/plan.md.bak"
  commit_doc "$change" fix-reopen "intent/demo/$change"
}

# T1 of an accepted two-task fix is built: the fix lands on the app's change branch and the build log of app records T1
# with that commit, committed through the program: build_first_task [change].
build_first_task() {
  local change="${1:-$fix_change}"
  local dir built
  dir="$(fix_folder "$change")"
  (
    cd app
    git switch -q -c "change/$change"
    sed -i.bak 's/Welcome back${2:+,$2}/Welcome back${2:+, $2}/' greet.sh && rm greet.sh.bak
    grep -q 'back${2:+, $2}' greet.sh
    git_in commit -q -m "fix: put a space after the comma" -- greet.sh
  )
  built=$(git -C app rev-parse --short HEAD)
  mkdir -p "$dir/build"
  cat > "$dir/build/app.md" <<MD
\`\`\`jsonc
{ "layout": 2 }
\`\`\`

# Build log: app

### T1

\`\`\`jsonc
{
  "task": "T1",
  "commits": ["$built"]
}
\`\`\`
MD
  commit_doc "$change" build-log "intent/demo/$change/build/app.md"
}

# The engineer asked for one more scenario and the fix was redrafted: the spec gains R1.2 (a hyphenated name keeps its
# hyphen), the plan covers it in T1, and the redraft is committed again as "draft fix": redraft_with_hyphen [change].
redraft_with_hyphen() {
  local change="${1:-$fix_change}"
  local dir spec plan anchor
  dir="$(fix_folder "$change")"
  spec=$(cat "$dir/spec.md")
  anchor='      Then the greeting is "Welcome back, Mina"'
  [[ "$spec" == *"$anchor"* ]]
  spec=${spec/"$anchor"/"$anchor"$'\n\n    @R1.2\n    Scenario: A returning visitor with a hyphenated first name\n      Given a returning visitor whose first name is Mary-Jane\n      When the visitor opens the site\n      Then the greeting is "Welcome back, Mary-Jane"'}
  printf '%s\n' "$spec" > "$dir/spec.md"
  plan=$(cat "$dir/plan.md")
  anchor='| R1.1 | a returning visitor named Mina is greeted "Welcome back, Mina" | T1 |'
  [[ "$plan" == *"$anchor"* ]]
  plan=${plan/"$anchor"/"$anchor"$'\n| R1.2 | a returning visitor named Mary-Jane is greeted "Welcome back, Mary-Jane" | T1 |'}
  anchor='"covers": ["R1.1"]'
  [[ "$plan" == *"$anchor"* ]]
  plan=${plan/"$anchor"/'"covers": ["R1.1", "R1.2"]'}
  printf '%s\n' "$plan" > "$dir/plan.md"
  commit_doc "$change" fix-draft "intent/demo/$change"
}
