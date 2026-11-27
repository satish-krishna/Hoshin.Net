#!/usr/bin/env bash
# A web app whose CLAUDE.md links to a page with two rules the code does not show (new visible text goes in the copy file, and every visible change adds a changelog line) and one style rule that is the builder's, not the plan's.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"
source "$(dirname "${BASH_SOURCE[0]}")/../shared/plan.sh"
plan_marker_without_hooks
plan_app_web rules
plan_change_to_spec 2026-11-01-greeting basic
