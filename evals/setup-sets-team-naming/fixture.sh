#!/usr/bin/env bash
# The base workspace, unchanged: a master repository already set up for system demo (one product repository, current
# context, no naming setting), so a naming-only run has a marker to change and nothing else to do.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"
