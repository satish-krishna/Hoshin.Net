#!/usr/bin/env bash
# The base workspace, unchanged: context rule C2 (a first-time visitor sees no greeting) is confirmed by the engineer,
# and the request would change it.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"
