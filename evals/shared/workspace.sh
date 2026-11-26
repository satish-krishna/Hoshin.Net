#!/usr/bin/env bash
# The base eval workspace, in Hoshin.Net's layout: a master repository set up for system demo (one product
# repository, app, with a current context file in JSONC front matter) and the product repository beside it. The case
# folder is the workspace, so the session starts in the folder that holds both. A case's fixture.sh sources this file,
# then adds what its case needs.
#
# The sandbox mounts /dev/null over dotfiles in the case folder; each repository lists them in .git/info/exclude so a
# check for uncommitted files does not refuse on them before the case tests anything.
set -euo pipefail
git_in() { git -c user.name=eval -c user.email=eval@example.com -c core.autocrlf=false "$@"; }
exclude_sandbox_mounts() {
  printf '/%s\n' .bash_profile .bashrc .claude/ .eval-artifacts .gitconfig .gitmodules .idea .mcp.json .profile .ripgreprc .vscode .zprofile .zshrc >> .git/info/exclude
}

mkdir master app
(
  cd app
  git init -q -b main
  exclude_sandbox_mounts
  cat > greet.sh <<'SH'
#!/usr/bin/env bash
# Prints the greeting for a visitor: greet.sh <returning|new> [first name]
[ "${1:-}" = "returning" ] || exit 0
echo "Welcome back${2:+, $2}"
SH
  cat > test.sh <<'SH'
#!/usr/bin/env bash
# The app's tests: bash test.sh
[ -z "$(bash greet.sh new)" ] || { echo "a first-time visitor was greeted"; exit 1; }
[ "$(bash greet.sh returning)" = "Welcome back" ] || { echo "no-name greeting is wrong"; exit 1; }
echo ok
SH
  git_in add . && git_in commit -q -m "feat: greet visitors"
)
app_head=$(git -C app rev-parse HEAD)
(
  cd master
  git init -q -b main
  exclude_sandbox_mounts
  cat > .hoshin.json <<'JSON'
{ "version": 1, "systems": { "demo": { "repos": ["app"] } }, "hooks": { "build.finish": [{ "id": "unit", "run": ["bash", "test.sh"] }] } }
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
  cat > intent/demo/context/app.md <<MD
\`\`\`jsonc
{ "repo": "app", "system": "demo", "source_commit": "$app_head", "built": "2026-11-26", "built_by": "eval" }
\`\`\`

# Context: app

The app greets visitors when they open the site.

## Rules the system enforces

| # | Rule | Provenance | Note |
|---|---|---|---|
| C1 | A returning visitor who gave a first name sees "Welcome back, " followed by that name | confirmed by engineer | |
| C2 | A first-time visitor sees no greeting | confirmed by engineer | |
MD
  git_in add . && git_in commit -q -m "chore(demo): set up hoshin for demo"
)
