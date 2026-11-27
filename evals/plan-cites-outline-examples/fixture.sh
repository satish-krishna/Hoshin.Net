#!/usr/bin/env bash
# A web app and a spec that states the greeting as one Scenario Outline over three conditions (returning, first name given, greetings turned off), whose Examples list every reachable combination with its outcome.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"
source "$(dirname "${BASH_SOURCE[0]}")/../shared/plan.sh"
plan_marker_without_hooks
plan_app_web contributing
plan_change_to_spec 2026-11-01-greeting outline
