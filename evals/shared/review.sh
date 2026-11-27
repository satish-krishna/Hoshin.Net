#!/usr/bin/env bash
# Fixture helpers for the review cases (B12). A review case starts from a change that is fully built: documents
# accepted, every plan task committed in the product repository with the engine's trailers, and the build log
# committed in the change's copy through the program. This file builds the base workspace (it sources workspace.sh),
# then defines helpers and the changes the cases share. A case's fixture.sh sources this file and calls one of them.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/workspace.sh"

# commit_docs <change> <kind> <path relative to the copy>...: commit_doc for several paths in one commit.
commit_docs() {
  local change="$1" kind="$2" out
  shift 2
  out="$(cd "$(copy_of "$change")" && hoshin commit --json --kind "$kind" --change "$change" -- "$@")"
  case "$out" in *'"committed":true'*) ;; *) echo "fixture: commit --kind $kind of $* refused: $out" >&2; exit 1 ;; esac
}

# The change's folder inside its copy, as a path relative to the copy: change_rel <change>.
change_rel() { echo "intent/demo/$1"; }

# pin_and_accept <change> <intent|spec|plan>: the pin the engine writes (the commit that last touched the upstream
# document), then status accepted, committed as the accept kind. The pin is written before the draft is committed by
# draft_and_accept, and after the drafts by accept_fix_drafts, as each flow does it.
pin_doc() {
  local change="$1" doc="$2" copy field up sha
  copy="$(copy_of "$change")"
  case "$doc" in
    spec) field=source_intent_commit up=intent ;;
    plan) field=source_spec_commit up=spec ;;
    *) return 0 ;;
  esac
  sha="$(git -C "$copy" log -1 --format=%H -- "$(change_rel "$change")/$up.md")"
  sed -i -E "s/(\"$field\": *\")[^\"]*\"/\1$sha\"/" "$copy/$(change_rel "$change")/$doc.md"
}
accept_doc() {
  local change="$1" doc="$2" copy
  copy="$(copy_of "$change")"
  pin_doc "$change" "$doc"
  sed -i 's/"status": "draft"/"status": "accepted"/' "$copy/$(change_rel "$change")/$doc.md"
  commit_docs "$change" "accept-$doc" "$(change_rel "$change")/$doc.md"
}

# draft_and_accept <change>: the full pipeline for the three documents already written into the copy as drafts, in the
# order the skills make them: each is pinned to its accepted upstream, committed as a draft, then accepted.
draft_and_accept() {
  local change="$1" doc kind
  for doc in intent spec plan; do
    case "$doc" in intent) kind=intent-draft ;; *) kind="$doc" ;; esac
    pin_doc "$change" "$doc"
    commit_docs "$change" "$kind" "$(change_rel "$change")/$doc.md"
    accept_doc "$change" "$doc"
  done
}

# accept_fix_drafts <change>: the light track. One draft commit for the three documents, then each accepted in turn.
accept_fix_drafts() {
  local change="$1" doc rel
  rel="$(change_rel "$change")"
  commit_docs "$change" fix-draft "$rel/intent.md" "$rel/spec.md" "$rel/plan.md"
  for doc in intent spec plan; do accept_doc "$change" "$doc"; done
}

# task_commit <repo dir> <T<n>> <subject>: commits what is staged with the engine's trailers.
task_commit() {
  git_in -C "$1" commit -q -m "$3" -m "Plan-Task: $2
Generated-By: hoshin"
}

# write_build_log <change> <repo> <T<n>> <red>: the one-entry build log of a repository, then committed through the
# program. <red> is the entry's `red` value, or empty for an entry that says nothing. With red, the `red` line is line
# 12, which review-reads-red-from-the-program points a finding at.
write_build_log() {
  local change="$1" repo="$2" task="$3" red="$4" first last copy tail
  copy="$(copy_of "$change")"
  first="$(git -C "$workspace_root/$repo" log --format=%h --grep="^Plan-Task: $task\$" | tail -1)"
  last="$(git -C "$workspace_root/$repo" log -1 --format=%h --grep="^Plan-Task: $task\$")"
  if [ -n "$red" ]; then tail=",
  \"red\": \"$red\" }"; else tail=" }"; fi
  mkdir -p "$copy/$(change_rel "$change")/build"
  cat > "$copy/$(change_rel "$change")/build/$repo.md" <<MD
\`\`\`jsonc
{ "layout": 2 }
\`\`\`

# Build log: $repo

### $task

\`\`\`jsonc
{ "task": "$task",
  "commits": ["$first", "$last"]$tail
\`\`\`
MD
  commit_docs "$change" build-log "$(change_rel "$change")/build/$repo.md"
}

# assert_ready_for_review <change>: the state the case promises, read from the program. Every task is logged and the
# review skill is not stopped.
assert_ready_for_review() {
  local out
  out="$(cd "$workspace_root/master" && hoshin state --json --system demo --change "$1")"
  case "$out" in
    *'"review":{'*) ;;
    *) echo "fixture: no review readiness in the state of $1: $out" >&2; exit 1 ;;
  esac
  case "$out" in
    *'"review":{"candidate":true,"stop":null'*) ;;
    *) echo "fixture: $1 is not ready for review: $out" >&2; exit 1 ;;
  esac
}

# The product repository's written rules, committed on main before the change: every shell script starts with a
# shebang and `set -euo pipefail`, and a lint script enforces it. The base scripts are rewritten to meet the rule, so
# a failure the lint finds later is the change's own. The CONTRIBUTING.md rule is on line 5.
app_follows_header_rule() {
  (
    cd "$workspace_root/app"
    cat > CONTRIBUTING.md <<'MD'
# Contributing

## Shell scripts

Every shell script starts with `#!/usr/bin/env bash` on its first line and `set -euo pipefail` on its second.
CI runs `bash scripts/lint.sh`, which fails on any script that does not.
MD
    mkdir -p scripts
    cat > scripts/lint.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
status=0
for f in $(git ls-files '*.sh'); do
  if [ "$(sed -n 1p "$f")" != '#!/usr/bin/env bash' ] || [ "$(sed -n 2p "$f")" != 'set -euo pipefail' ]; then
    echo "$f: missing the script header (CONTRIBUTING.md:5)"
    status=1
  fi
done
exit $status
SH
    cat > greet.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# Prints the greeting for a visitor: greet.sh <returning|new> [first name]
[ "${1:-}" = "returning" ] || exit 0
echo "Welcome back${2:+, $2}"
SH
    cat > test.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# The app's tests: bash test.sh
fail=0
run() {
  if "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi
}
returning_without_a_name() { [ "$(bash greet.sh returning)" = "Welcome back" ]; }
first_time_visitor() { [ -z "$(bash greet.sh new)" ]; }
run "a returning visitor without a name is greeted without a name" returning_without_a_name
run "a first-time visitor sees no greeting" first_time_visitor
exit "$fail"
SH
    git_in add . && git_in commit -q -m "docs: contributing rules and the script header"
  )
}

# vip_greeting_change <change> <red>: a full-track change of one task, built and logged. T1 adds a vip greeting in a
# new script, vip.sh, which has no header: that breaks the written rule (CONTRIBUTING.md:5) and the lint script, so a
# reviewer who reads the rules has one convention finding to record and nothing else. <red> is the build log's red
# value ("observed" or "not observed: <reason>").
vip_greeting_change() {
  local change="$1" red="$2" copy app_head
  app_follows_header_rule
  app_head="$(git -C "$workspace_root/app" rev-parse --short=7 HEAD)"
  change_start "$change" feat demo
  copy="$(copy_of "$change")"
  mkdir -p "$copy/$(change_rel "$change")"
  cat > "$copy/$(change_rel "$change")/intent.md" <<MD
\`\`\`jsonc
{ "title": "Greet vip visitors",
  "system": "demo",
  "author": "Pat Owner, Product Owner",
  "date": "2026-11-01",
  "status": "draft",
  "scope": "demo",
  "context": ["app@$app_head"] }
\`\`\`

# Intent: Greet vip visitors

## Problem

Vip visitors get the same greeting as every other returning visitor, so the site does not show that we know who they are.

## Proposed outcome

1. A vip visitor who gave a first name sees "Welcome back, " followed by the name and ", our guest of honor".

**Success looks like:** every vip visitor with a first name sees the guest-of-honor greeting.

## Affected users and systems

- **Users:** vip visitors.
- **Systems:** the app.

## Constraints

- The greeting uses only the first name the visitor already gave us.

## Decisions

| # | Situation | Decision | What should happen |
|---|---|---|---|
| D1 | A vip visitor gave no first name | out of scope | - |

## Open questions

- None.
MD
  cat > "$copy/$(change_rel "$change")/spec.md" <<'MD'
```jsonc
{ "layout": 2,
  "source_intent": "CHANGE/intent.md",
  "source_intent_commit": "pending acceptance",
  "system": "demo",
  "date": "2026-11-01",
  "status": "draft",
  "policies_applied": [],
  "policies_not_checked": ["security", "compliance", "brand", "UX"] }
```

# Spec: Greet vip visitors

## Requirements

```gherkin
Feature: Greet vip visitors
  A vip visitor who gave a first name sees a greeting that names them a guest of honor.

  @R1 @from:outcome-1
  Rule: A vip visitor with a first name is greeted as a guest of honor

    @R1.1
    Scenario: A vip visitor with a first name
      Given a vip visitor whose first name is Mina
      When the visitor opens the site
      Then the greeting is "Welcome back, Mina, our guest of honor"
```

## Out of scope

- D1: a vip visitor with no first name.

## Design

The app builds the greeting in greet.sh, which hands the vip kind of visitor to a new script. The greetings for returning and first-time visitors are unchanged.

## Areas of concern

1. **No policy skills were applied.** Owner: product owner. Review the unchecked areas.
MD
  sed -i "s#CHANGE/intent.md#$change/intent.md#" "$copy/$(change_rel "$change")/spec.md"
  cat > "$copy/$(change_rel "$change")/plan.md" <<MD
\`\`\`jsonc
{ "layout": 2,
  "source_spec": "$change/spec.md",
  "source_spec_commit": "pending acceptance",
  "system": "demo",
  "date": "2026-11-01",
  "author": "Eve Engineer",
  "status": "draft" }
\`\`\`

# Plan: Greet vip visitors

## Approach

One task in the app: add the vip greeting.

## Scenario coverage

| Scenario | Test | Task |
|---|---|---|
| R1.1 | a vip visitor with a first name is greeted as a guest of honor | T1 |

## Tasks

### T1: Greet vip visitors

\`\`\`jsonc
{ "id": "T1",
  "repo": "app",
  "depends_on": [],
  "uses": [],
  "files": ["greet.sh", "vip.sh", "test.sh"],
  "covers": ["R1.1"],
  "supports": [],
  "test": ["bash", "test.sh"] }
\`\`\`

#### Change

Hand the vip kind of visitor from greet.sh to a new script, vip.sh, which prints "Welcome back, <name>, our guest of honor". The greetings for returning and first-time visitors stay as they are.

#### Tests

Add to test.sh a check that \`bash greet.sh vip Mina\` prints "Welcome back, Mina, our guest of honor". It fails before the change because greet.sh prints nothing for the vip kind. Run \`bash test.sh\`.

#### Done when

\`bash test.sh\` exits 0.
MD
  draft_and_accept "$change"

  # The task, built test first in the product repository, with the engine's trailers.
  (
    cd "$workspace_root/app"
    git checkout -q -b "change/$change"
    cat > test.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# The app's tests: bash test.sh
fail=0
run() {
  if "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi
}
vip_with_a_name() { [ "$(bash greet.sh vip Mina)" = "Welcome back, Mina, our guest of honor" ]; }
returning_without_a_name() { [ "$(bash greet.sh returning)" = "Welcome back" ]; }
first_time_visitor() { [ -z "$(bash greet.sh new)" ]; }
run "a vip visitor with a name is greeted as a guest of honor" vip_with_a_name
run "a returning visitor without a name is greeted without a name" returning_without_a_name
run "a first-time visitor sees no greeting" first_time_visitor
exit "$fail"
SH
    git_in add test.sh
    task_commit . T1 'test(demo): fail until the vip greeting exists'
    cat > greet.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# Prints the greeting for a visitor: greet.sh <returning|new|vip> [first name]
[ "${1:-}" != "vip" ] || exec bash vip.sh "${2:-}"
[ "${1:-}" = "returning" ] || exit 0
echo "Welcome back${2:+, $2}"
SH
    cat > vip.sh <<'SH'
echo "Welcome back, ${1:-friend}, our guest of honor"
SH
    git_in add greet.sh vip.sh
    task_commit . T1 'feat(demo): greet vip visitors as guests of honor'
  )
  write_build_log "$change" app T1 "$red"
  assert_ready_for_review "$change"
}

# light_vip_change <change> <protecting|two>: a built, logged light-track fix of one task (track: fix), on the repository
# of vip_greeting_change, whose scripts all follow its written rule, so the reviewer has nothing else to report.
#   protecting: the task has one scenario and one new failing test; its test commit also makes two existing, passing tests
#     stricter with a precondition that fails until the production commit, so three tests fail in it, but only one asserts
#     a new behavior. A protecting test is not a new behavior.
#   two: the task covers two scenarios, a vip visitor and a staff visitor, and its test commit adds one failing test for
#     each: two new behaviors in one task, over the light track's cap of one.
light_vip_change() {
  local change="$1" variant="$2" copy rel app_head title outcome scenarios coverage covers tests
  app_follows_header_rule
  app_head="$(git -C "$workspace_root/app" rev-parse --short=7 HEAD)"
  change_start "$change" feat demo
  copy="$(copy_of "$change")"
  rel="$(change_rel "$change")"
  mkdir -p "$copy/$rel"
  if [ "$variant" = protecting ]; then
    title="Greet vip visitors" covers='"R1.1"'
    outcome='A vip visitor who gave a first name sees "Welcome back, " followed by the name and ", our guest of honor".'
    scenarios='
  @R1 @from:problem
  Rule: A vip visitor with a first name is greeted as a guest of honor

    @R1.1
    Scenario: A vip visitor with a first name
      Given a vip visitor whose first name is Mina
      When the visitor opens the site
      Then the greeting is "Welcome back, Mina, our guest of honor"'
    coverage='| R1.1 | a vip visitor with a name is greeted as a guest of honor | T1 |'
    tests='One new failing test: add to test.sh a check that `bash greet.sh vip Mina` prints "Welcome back, Mina, our guest of honor". Run `bash test.sh`. It fails before the change because greet.sh prints nothing for the vip kind. The checks that `bash greet.sh returning` prints "Welcome back" and that `bash greet.sh new` prints nothing already pass before the change and protect the returning and first-time greetings, so they are not new tests.'
  else
    title="Greet vip and staff visitors" covers='"R1.1", "R2.1"'
    outcome='A vip visitor who gave a first name sees "Welcome back, " followed by the name and ", our guest of honor", and a staff visitor who gave a first name sees "Hello, " followed by the name and " (staff)".'
    scenarios='
  @R1 @from:problem
  Rule: A vip visitor with a first name is greeted as a guest of honor

    @R1.1
    Scenario: A vip visitor with a first name
      Given a vip visitor whose first name is Mina
      When the visitor opens the site
      Then the greeting is "Welcome back, Mina, our guest of honor"

  @R2 @from:problem
  Rule: A staff visitor with a first name is greeted as staff

    @R2.1
    Scenario: A staff visitor with a first name
      Given a staff visitor whose first name is Mina
      When the visitor opens the site
      Then the greeting is "Hello, Mina (staff)"'
    coverage='| R1.1 | a vip visitor with a name is greeted as a guest of honor | T1 |
| R2.1 | a staff visitor with a name is greeted as staff | T1 |'
    tests='Two new failing tests: add to test.sh a check that `bash greet.sh vip Mina` prints "Welcome back, Mina, our guest of honor", and a check that `bash greet.sh staff Mina` prints "Hello, Mina (staff)". Run `bash test.sh`. Both fail before the change because greet.sh prints nothing for either kind. The check that `bash greet.sh returning` prints "Welcome back" already passes before the change and protects the returning greeting, so it is not a new test.'
  fi
  cat > "$copy/$rel/intent.md" <<MD
\`\`\`jsonc
{ "title": "$title",
  "system": "demo",
  "author": "Eve Engineer, engineer",
  "date": "2026-11-01",
  "status": "draft",
  "scope": "demo",
  "track": "fix",
  "kind": "enhancement",
  "context": ["app@$app_head"] }
\`\`\`

# Intent: $title

## Problem

Vip visitors and staff get the same greeting as every other returning visitor, so the site does not show that we know who they are.

## Proposed outcome

$outcome

## Affected users and systems

- **Users:** vip visitors and staff.
- **Systems:** the app, the greeting script.

## Constraints

- The greetings for returning and first-time visitors stay as they are.

## Decisions

None.

## Open questions

None.
MD
  cat > "$copy/$rel/spec.md" <<MD
\`\`\`jsonc
{ "layout": 2,
  "source_intent": "$change/intent.md",
  "source_intent_commit": "pending acceptance",
  "system": "demo",
  "date": "2026-11-01",
  "status": "draft",
  "policies_applied": [],
  "policies_not_checked": ["security", "compliance", "brand", "UX"] }
\`\`\`

# Spec: $title

## Requirements

\`\`\`gherkin
Feature: $title
  Visitors of a special kind see their own greeting.
$scenarios
\`\`\`

## Design

The greeting is built in greet.sh. The change adds the special kinds of visitor. The greetings for returning and first-time visitors are unchanged.

## Areas of concern

1. **Policy areas not checked.** The light track does not apply policy skills. Owner: the engineer who accepted the fix.
MD
  cat > "$copy/$rel/plan.md" <<MD
\`\`\`jsonc
{ "layout": 2,
  "source_spec": "$change/spec.md",
  "source_spec_commit": "pending acceptance",
  "system": "demo",
  "date": "2026-11-01",
  "author": "Eve Engineer",
  "status": "draft" }
\`\`\`

# Plan: $title

## Approach

Add the failing tests first, then the greetings to greet.sh.

## Scenario coverage

| Scenario | Test | Task |
|---|---|---|
$coverage

## Tasks

### T1: $title

\`\`\`jsonc
{ "id": "T1",
  "repo": "app",
  "depends_on": [],
  "uses": [],
  "files": ["greet.sh", "test.sh"],
  "covers": [$covers],
  "supports": [],
  "test": ["bash", "test.sh"] }
\`\`\`

#### Change

Add the special kinds of visitor to greet.sh. The greetings for returning and first-time visitors stay as they are.

#### Tests

$tests

#### Done when

\`bash test.sh\` exits 0.
MD
  accept_fix_drafts "$change"

  (
    cd "$workspace_root/app"
    git checkout -q -b "change/$change"
    if [ "$variant" = protecting ]; then
      cat > test.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# The app's tests: bash test.sh
fail=0
run() {
  if "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi
}
vip_with_a_name() { [ "$(bash greet.sh vip Mina)" = "Welcome back, Mina, our guest of honor" ]; }
returning_without_a_name() {
  # The vip kind must exist before the plain greeting can be compared against it.
  [ -n "$(bash greet.sh vip Mina)" ] || return 1
  [ "$(bash greet.sh returning)" = "Welcome back" ]
}
first_time_visitor() {
  # The vip kind must exist before the first-time greeting can be compared against it.
  [ -n "$(bash greet.sh vip Mina)" ] || return 1
  [ -z "$(bash greet.sh new)" ]
}
run "a vip visitor with a name is greeted as a guest of honor" vip_with_a_name
run "a returning visitor without a name is greeted without a name" returning_without_a_name
run "a first-time visitor sees no greeting" first_time_visitor
exit "$fail"
SH
      git_in add test.sh
      task_commit . T1 'test(demo): fail until the vip greeting exists'
      cat > greet.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# Prints the greeting for a visitor: greet.sh <returning|new|vip> [first name]
case "${1:-}" in
  returning) echo "Welcome back${2:+, $2}" ;;
  vip) echo "Welcome back, ${2:-friend}, our guest of honor" ;;
  *) exit 0 ;;
esac
SH
      git_in add greet.sh
      task_commit . T1 'feat(demo): greet vip visitors as guests of honor'
    else
      cat > test.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# The app's tests: bash test.sh
fail=0
run() {
  if "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi
}
vip_with_a_name() { [ "$(bash greet.sh vip Mina)" = "Welcome back, Mina, our guest of honor" ]; }
staff_with_a_name() { [ "$(bash greet.sh staff Mina)" = "Hello, Mina (staff)" ]; }
returning_without_a_name() { [ "$(bash greet.sh returning)" = "Welcome back" ]; }
first_time_visitor() { [ -z "$(bash greet.sh new)" ]; }
run "a vip visitor with a name is greeted as a guest of honor" vip_with_a_name
run "a staff visitor with a name is greeted as staff" staff_with_a_name
run "a returning visitor without a name is greeted without a name" returning_without_a_name
run "a first-time visitor sees no greeting" first_time_visitor
exit "$fail"
SH
      git_in add test.sh
      task_commit . T1 'test(demo): fail until the vip and staff greetings exist'
      cat > greet.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# Prints the greeting for a visitor: greet.sh <returning|new|vip|staff> [first name]
case "${1:-}" in
  returning) echo "Welcome back${2:+, $2}" ;;
  vip) echo "Welcome back, ${2:-friend}, our guest of honor" ;;
  staff) echo "Hello, ${2:-colleague} (staff)" ;;
  *) exit 0 ;;
esac
SH
      git_in add greet.sh
      task_commit . T1 'feat(demo): greet vip and staff visitors'
    fi
  )
  write_build_log "$change" app T1 observed
  assert_ready_for_review "$change"
}

# A second product repository, lib, beside app, added to system demo in the marker and committed through the program
# (the setup kind). The base fixture has one product repository, so a two-repository review starts here. lib has its own
# written rule, on line 5 of its own CONTRIBUTING.md, that differs from app's: a script's second line is a usage
# comment. Its tests and its lint script follow it.
add_lib_repository() {
  (
    cd "$workspace_root"
    mkdir lib
    cd lib
    git init -q -b main
    exclude_sandbox_mounts
    cat > CONTRIBUTING.md <<'MD'
# Contributing

## Shell scripts

Every shell script starts with `#!/usr/bin/env bash` and has a `# Usage:` comment on its second line.
CI runs `bash scripts/lint.sh`, which fails on any script that does not.
MD
    mkdir -p scripts
    cat > scripts/lint.sh <<'SH'
#!/usr/bin/env bash
# Usage: bash scripts/lint.sh   Fails on a script without the header.
set -euo pipefail
status=0
for f in $(git ls-files '*.sh'); do
  if [ "$(sed -n 1p "$f")" != '#!/usr/bin/env bash' ] || [[ "$(sed -n 2p "$f")" != '# Usage:'* ]]; then
    echo "$f: missing the script header (CONTRIBUTING.md:5)"
    status=1
  fi
done
exit $status
SH
    cat > format.sh <<'SH'
#!/usr/bin/env bash
# Usage: bash format.sh <text>   Prints the text without surrounding spaces.
set -euo pipefail
printf '%s\n' "${1:-}" | sed 's/^ *//;s/ *$//'
SH
    cat > test.sh <<'SH'
#!/usr/bin/env bash
# Usage: bash test.sh   Runs the library's tests.
set -euo pipefail
[ "$(bash format.sh '  hi ')" = "hi" ] || { echo "format.sh did not trim"; exit 1; }
echo ok
SH
    git_in add . && git_in commit -q -m "feat: format text"
  )
  local lib_head
  lib_head="$(git -C "$workspace_root/lib" rev-parse HEAD)"
  (
    cd "$workspace_root/master"
    cat > .hoshin.json <<'JSON'
{ "version": 1, "systems": { "demo": { "repos": ["app", "lib"] } }, "hooks": { "build.finish": [{ "id": "unit", "run": ["bash", "test.sh"] }] } }
JSON
    cat > intent/demo/context/lib.md <<MD
\`\`\`jsonc
{ "repo": "lib", "system": "demo", "source_commit": "$lib_head", "built": "2026-11-26", "built_by": "eval" }
\`\`\`

# Context: lib

The library formats the text the app shows.

## Rules the system enforces

| # | Rule | Provenance | Note |
|---|---|---|---|
| C1 | Text is shown without surrounding spaces | confirmed by engineer | |
MD
    hoshin commit --json --kind setup --system demo -- .hoshin.json intent/demo/context/lib.md | grep -q "\"committed\":true"
  )
}

# two_repository_change <change>: a full-track change of two tasks, one in each repository, built and logged. T1 (app)
# adds a vip greeting in a new script that has no header, which breaks app's written rule; T2 (lib) adds a farewell in
# a new script that has no usage comment, which breaks lib's different rule. The tasks are independent, so each
# reviewer has exactly one convention finding in its own repository to record, citing that repository's own file.
two_repository_change() {
  local change="$1" copy rel app_head
  app_follows_header_rule
  add_lib_repository
  app_head="$(git -C "$workspace_root/app" rev-parse --short=7 HEAD)"
  change_start "$change" feat demo
  copy="$(copy_of "$change")"
  rel="$(change_rel "$change")"
  mkdir -p "$copy/$rel"
  cat > "$copy/$rel/intent.md" <<MD
\`\`\`jsonc
{ "title": "Greet and thank vip visitors",
  "system": "demo",
  "author": "Pat Owner, Product Owner",
  "date": "2026-11-01",
  "status": "draft",
  "scope": "demo",
  "context": ["app@$app_head"] }
\`\`\`

# Intent: Greet and thank vip visitors

## Problem

Vip visitors are treated like every other visitor when they arrive and when they leave, so the site does not show that we know who they are.

## Proposed outcome

1. A vip visitor who gave a first name sees "Welcome back, " followed by the name and ", our guest of honor".
2. A vip visitor who is leaving sees "Thank you for visiting, " followed by the first name.

**Success looks like:** every vip visitor is greeted and thanked by name.

## Affected users and systems

- **Users:** vip visitors.
- **Systems:** the app, which greets, and the library, which holds the farewell.

## Constraints

- The messages use only the first name the visitor already gave us.

## Decisions

| # | Situation | Decision | What should happen |
|---|---|---|---|
| D1 | A vip visitor gave no first name | out of scope | - |

## Open questions

- None.
MD
  cat > "$copy/$rel/spec.md" <<MD
\`\`\`jsonc
{ "layout": 2,
  "source_intent": "$change/intent.md",
  "source_intent_commit": "pending acceptance",
  "system": "demo",
  "date": "2026-11-01",
  "status": "draft",
  "policies_applied": [],
  "policies_not_checked": ["security", "compliance", "brand", "UX"] }
\`\`\`

# Spec: Greet and thank vip visitors

## Requirements

\`\`\`gherkin
Feature: Greet and thank vip visitors
  A vip visitor who gave a first name is greeted on arrival and thanked on leaving.

  @R1 @from:outcome-1
  Rule: A vip visitor with a first name is greeted as a guest of honor

    @R1.1
    Scenario: A vip visitor with a first name arrives
      Given a vip visitor whose first name is Mina
      When the visitor opens the site
      Then the greeting is "Welcome back, Mina, our guest of honor"

  @R2 @from:outcome-2
  Rule: A vip visitor with a first name is thanked on leaving

    @R2.1
    Scenario: A vip visitor with a first name leaves
      Given a vip visitor whose first name is Mina
      When the visitor leaves the site
      Then the farewell is "Thank you for visiting, Mina"
\`\`\`

## Out of scope

- D1: a vip visitor with no first name.

## Design

The app builds the greeting in greet.sh, which hands the vip kind of visitor to a new script. The library holds the farewell in a new script of its own. The two do not depend on each other.

## Areas of concern

1. **No policy skills were applied.** Owner: product owner. Review the unchecked areas.
MD
  cat > "$copy/$rel/plan.md" <<MD
\`\`\`jsonc
{ "layout": 2,
  "source_spec": "$change/spec.md",
  "source_spec_commit": "pending acceptance",
  "system": "demo",
  "date": "2026-11-01",
  "author": "Eve Engineer",
  "status": "draft" }
\`\`\`

# Plan: Greet and thank vip visitors

## Approach

Two independent tasks, one in each repository: the vip greeting in the app, and the farewell in the library.

## Scenario coverage

| Scenario | Test | Task |
|---|---|---|
| R1.1 | a vip visitor with a name is greeted as a guest of honor | T1 |
| R2.1 | a vip visitor with a name is thanked on leaving | T2 |

## Tasks

### T1: Greet vip visitors (\`app\`)

\`\`\`jsonc
{ "id": "T1",
  "repo": "app",
  "depends_on": [],
  "uses": [],
  "files": ["greet.sh", "vip.sh", "test.sh"],
  "covers": ["R1.1"],
  "supports": [],
  "test": ["bash", "test.sh"] }
\`\`\`

#### Change

Hand the vip kind of visitor from greet.sh to a new script, vip.sh, which prints "Welcome back, <name>, our guest of honor". The other greetings stay as they are.

#### Tests

Add to test.sh a check that \`bash greet.sh vip Mina\` prints "Welcome back, Mina, our guest of honor". It fails before the change because greet.sh prints nothing for the vip kind. Run \`bash test.sh\`.

#### Done when

\`bash test.sh\` exits 0.

### T2: Thank vip visitors (\`lib\`)

\`\`\`jsonc
{ "id": "T2",
  "repo": "lib",
  "depends_on": [],
  "uses": [],
  "files": ["farewell.sh", "test.sh"],
  "covers": ["R2.1"],
  "supports": [],
  "test": ["bash", "test.sh"] }
\`\`\`

#### Change

Add farewell.sh, which prints "Thank you for visiting, <name>".

#### Tests

Add to test.sh a check that \`bash farewell.sh Mina\` prints "Thank you for visiting, Mina". It fails before the change because farewell.sh does not exist. Run \`bash test.sh\`.

#### Done when

\`bash test.sh\` exits 0.
MD
  draft_and_accept "$change"

  (
    cd "$workspace_root/app"
    git checkout -q -b "change/$change"
    cat > test.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# The app's tests: bash test.sh
fail=0
run() {
  if "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi
}
vip_with_a_name() { [ "$(bash greet.sh vip Mina)" = "Welcome back, Mina, our guest of honor" ]; }
returning_without_a_name() { [ "$(bash greet.sh returning)" = "Welcome back" ]; }
first_time_visitor() { [ -z "$(bash greet.sh new)" ]; }
run "a vip visitor with a name is greeted as a guest of honor" vip_with_a_name
run "a returning visitor without a name is greeted without a name" returning_without_a_name
run "a first-time visitor sees no greeting" first_time_visitor
exit "$fail"
SH
    git_in add test.sh
    task_commit . T1 'test(demo): fail until the vip greeting exists'
    cat > greet.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# Prints the greeting for a visitor: greet.sh <returning|new|vip> [first name]
[ "${1:-}" != "vip" ] || exec bash vip.sh "${2:-}"
[ "${1:-}" = "returning" ] || exit 0
echo "Welcome back${2:+, $2}"
SH
    cat > vip.sh <<'SH'
echo "Welcome back, ${1:-friend}, our guest of honor"
SH
    git_in add greet.sh vip.sh
    task_commit . T1 'feat(demo): greet vip visitors as guests of honor'
  )
  (
    cd "$workspace_root/lib"
    git checkout -q -b "change/$change"
    cat > test.sh <<'SH'
#!/usr/bin/env bash
# Usage: bash test.sh   Runs the library's tests.
set -euo pipefail
[ "$(bash format.sh '  hi ')" = "hi" ] || { echo "format.sh did not trim"; exit 1; }
[ "$(bash farewell.sh Mina)" = "Thank you for visiting, Mina" ] || { echo "farewell.sh is wrong"; exit 1; }
echo ok
SH
    git_in add test.sh
    task_commit . T2 'test(demo): fail until the farewell exists'
    cat > farewell.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
echo "Thank you for visiting, ${1:-friend}"
SH
    git_in add farewell.sh
    task_commit . T2 'feat(demo): thank vip visitors on leaving'
  )
  write_build_log "$change" app T1 observed
  write_build_log "$change" lib T2 observed
  assert_ready_for_review "$change"
}
