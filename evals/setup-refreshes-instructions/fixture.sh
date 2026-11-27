#!/usr/bin/env bash
# The base workspace, but the instructions are out of date: the master holds a stale intent/workspace.md (committed, as
# another engineer's setup run would have left it) and the workspace CLAUDE.md holds an old hoshin block between two of
# the engineer's own sections, so a refresh has to replace the block and nothing around it.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"

# History the engine never makes: a workspace.md that describes a system that no longer exists. Committed by hand, under a
# subject of its own, so the case can tell it from the refresh commit the run makes through the program.
(
  cd master
  cat > intent/workspace.md <<'MD'
# hoshin workspace

OLD INSTRUCTIONS FOR A SYSTEM THAT NO LONGER EXISTS
MD
  git_in add intent/workspace.md && git_in commit -q -m "chore(demo): write workspace instructions"
)

cat > CLAUDE.md <<'MD'
# My notes

Keep this line exactly as it is.

<!-- hoshin:start -->
OLD INSTRUCTIONS FOR A SYSTEM THAT NO LONGER EXISTS
<!-- hoshin:end -->

## After

Keep this line too.
MD
