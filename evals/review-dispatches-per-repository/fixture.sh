#!/usr/bin/env bash
# A built full-track change of two tasks in two repositories, app and lib (added to the base workspace and to the
# system in the marker by the shared helper). Each repository has its own written rule, and each task's new script
# breaks its repository's rule, so there is one convention finding to record in each.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/review.sh"
two_repository_change 2026-11-01-vip-greeting-and-farewell
