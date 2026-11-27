#!/usr/bin/env bash
# The built change of review-commits-findings, but the only task's build log entry says its red was not observed. The
# entry's `red` is line 12 of the log, so the review has a red line to read from the program's brief and to point at.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/review.sh"
vip_greeting_change 2026-11-01-vip-greeting "not observed: no red recorded for T1 on this machine"
