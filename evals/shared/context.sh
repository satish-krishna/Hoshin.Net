#!/usr/bin/env bash
# Sourced by the context cases' fixtures. It takes the base workspace's helpers and replaces its repositories with ones
# whose history is fixed: every product commit carries a set date, so the commit ids are the same on every machine and
# a case can pin the id the context file's source_commit must hold. A grader can't read the program's brief, and a model
# that wrote the master's HEAD, or an invented id, would otherwise pass a pattern that only checks for 40 hex digits.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/workspace.sh"

rm -rf app master
mkdir app master

# An app commit on a fixed date: app_commit <ISO date> <message>. The files are already written in app/.
app_commit() {
  (cd app && GIT_AUTHOR_DATE="$1" GIT_COMMITTER_DATE="$1" git_in add -A && GIT_AUTHOR_DATE="$1" GIT_COMMITTER_DATE="$1" git_in commit -q -m "$2")
}

# The product repository, ready for its first commit.
init_app() {
  (cd app && git init -q -b main && exclude_sandbox_mounts)
}

# The master repository: the marker and the policies, committed as setup does. A case writes the context file after.
init_master() {
  (
    cd master
    git init -q -b main
    exclude_sandbox_mounts
    cat > .hoshin.json <<'JSON'
{ "version": 1, "systems": { "demo": { "repos": ["app"] } } }
JSON
    mkdir -p intent/demo/context
    cat > intent/demo/policies.md <<'MD'
# Policies: demo

| Area | Skill | Owner |
|---|---|---|
| security | none | security lead |
| compliance | none | compliance lead |
| brand | none | brand lead |
| UX | none | design lead |
MD
    git_in add . && git_in commit -q -m "chore(demo): set up hoshin for demo"
  )
}

# Commits context files in the master through the program, as a context run's Stop does: commit_context <path>...
commit_context() {
  local out
  out="$(cd "$workspace_root/master" && hoshin commit --json --kind context --system demo -- "$@")"
  case "$out" in *'"committed":true'*) ;; *) echo "fixture: commit --kind context of $* refused: $out" >&2; exit 1 ;; esac
}

app_head() { git -C "$workspace_root/app" rev-parse HEAD; }
