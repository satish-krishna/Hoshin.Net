#!/usr/bin/env bash
# A built light-track fix of one task covering two scenarios, a vip visitor and a staff visitor. Its test commit adds
# one failing test for each: two new behaviors in one task, over the light track's cap. Every written rule of the
# repository is met, so the cap is the only thing to report.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/review.sh"
light_vip_change 2026-11-01-vip-staff-greeting two
