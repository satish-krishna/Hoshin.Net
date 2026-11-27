#!/usr/bin/env bash
# The base workspace, whose marker already has one hook (build.finish), plus the system's own hooks file, empty and
# committed, so a grader can read it whether or not the run writes to it. A hooks-only run is then asked for a hook on
# task.done, a hook point that is deprecated.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"

# History the engine never makes: a hooks file that holds no hook. Committed by hand, under a subject of its own, so that
# "the run committed nothing" reads as the reflog still ending with it.
(
  cd master
  echo '{ "version": 1, "hooks": {} }' > intent/demo/hooks.json
  git_in add intent/demo/hooks.json && git_in commit -q -m "chore(demo): add an empty hooks file"
  hoshin check intent/demo/hooks.json > /dev/null
)
