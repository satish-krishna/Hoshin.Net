#!/usr/bin/env bash
# The accepted two-task fix with T1 built and logged, then reopened (typed-only, so made here through the program). The
# spec has a second scenario (R1.2), the plan a second task (T2, still to build), and the build log of app holds an entry
# for T1 and the commit that built it, on the app's change branch. Spec and plan are back to draft, the intent accepted.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/fix.sh"
buggy_greeting
draft_fix "$fix_change" two
accept_fix
build_first_task
reopen_fix
