#!/usr/bin/env bash
# The base workspace (a master repository already set up for system demo, with current context and one product
# repository) plus a workspace CLAUDE.md that holds the engineer's own notes, which setup must leave alone. There is no
# intent/workspace.md yet, so a rerun of setup has the instructions to write.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"

cat > CLAUDE.md <<'MD'
# My notes

Keep this line exactly as it is.
MD
