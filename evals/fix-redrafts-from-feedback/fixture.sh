#!/usr/bin/env bash
# The workspace of fix-accepts-a-bug-fix, after the fix skill drafted the fix and stopped before acceptance: the change
# holds intent, spec and plan as drafts (track fix, pins pending acceptance), committed as "draft fix" in the change's
# copy. The product repository is untouched.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/fix.sh"
buggy_greeting
draft_fix
