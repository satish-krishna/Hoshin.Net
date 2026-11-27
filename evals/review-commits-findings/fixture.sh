#!/usr/bin/env bash
# A fully built full-track change of one task. The product repository's CONTRIBUTING.md requires a header on every shell
# script and its lint script enforces it; the change's new script lacks it, so a reviewer who reads the rules has a
# convention finding to record.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/review.sh"
vip_greeting_change 2026-11-01-vip-greeting observed
