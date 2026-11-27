#!/usr/bin/env bash
# The base workspace plus a system plan template that is not the plugin's layout, committed on main before the change
# starts so both the master and the change's copy hold it. The change's intent and spec are accepted with holding pins.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"
source "$(dirname "${BASH_SOURCE[0]}")/../shared/plan.sh"
(
  cd master
  mkdir -p intent/demo/templates
  cat > intent/demo/templates/plan.md <<'MD'
---
source_spec: <change>/spec.md
system: <system>
status: draft
---

# SYSTEM-TEMPLATE-MARKER: <title>

## House sections

- Put every task in a table.
MD
  git_in add . && git_in commit -q -m "chore(demo): add a plan template"
)
plan_change_to_spec
