#!/usr/bin/env bash
# Sourced by the start cases' fixtures: the base workspace with the original suite's greeting bug. greet.sh drops the
# space after the comma, so a returning visitor named Mina sees "Welcome back,Mina", which breaks context rule C1.
# The base's repositories are made again, because the context file pins the product repository's commit
# (source_commit): changing greet.sh afterwards would make the context stale, and the cases are not about staleness.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/workspace.sh"

rm -rf app/.git master/.git
sed 's/\${2:+, \$2}/${2:+,$2}/' app/greet.sh > app/greet.sh.new && mv app/greet.sh.new app/greet.sh
(
  cd app
  git init -q -b main
  exclude_sandbox_mounts
  git_in add . && git_in commit -q -m "feat: greet visitors"
)
app_head=$(git -C app rev-parse HEAD)
sed "s/\"source_commit\": \"[0-9a-f]*\"/\"source_commit\": \"$app_head\"/" master/intent/demo/context/app.md > master/intent/demo/context/app.md.new
mv master/intent/demo/context/app.md.new master/intent/demo/context/app.md
(
  cd master
  git init -q -b main
  exclude_sandbox_mounts
  git_in add . && git_in commit -q -m "chore(demo): set up hoshin for demo"
)
