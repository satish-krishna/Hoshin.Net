#!/usr/bin/env bash
# The workspace of fix-accepts-a-bug-fix. This case asks for a feature that stays in one repository and touches no
# context rule, but needs far more than three tasks.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/fix.sh"
buggy_greeting
