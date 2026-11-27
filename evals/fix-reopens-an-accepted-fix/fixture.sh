#!/usr/bin/env bash
# The drafted fix of fix-redrafts-from-feedback after the engineer accepted it (intent, spec and plan, each in its own
# commit, every pin holding) and then reopened it. Reopening is typed-only, so the fixture makes the reopen commit
# through the program the way a typed reopen leaves it: spec and plan back to draft, the intent still accepted.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/fix.sh"
buggy_greeting
draft_fix
accept_fix
reopen_fix
