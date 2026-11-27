#!/usr/bin/env bash
# A web app whose contributing guide asks for a production build and a browser check of every route before a merge, a marker with no build.finish hooks, and a change with an accepted intent and spec. The system has no build.finish hooks, so the plan has to name the whole-app checks itself.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"
source "$(dirname "${BASH_SOURCE[0]}")/../shared/plan.sh"
plan_marker_without_hooks
plan_app_web contributing
plan_change_to_spec 2026-11-01-greeting basic
