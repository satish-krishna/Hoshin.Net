#!/usr/bin/env bash
# The base workspace, unchanged: the run starts an intent in it, which is enough for the hooks to start a run and
# write their trace.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"
