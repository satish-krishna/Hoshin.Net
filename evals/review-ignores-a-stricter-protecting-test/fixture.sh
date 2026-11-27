#!/usr/bin/env bash
# A built light-track fix of one task covering one scenario. The task's test commit adds one new failing test and also
# makes two existing tests stricter with a precondition that fails until the production commit, so three tests fail in
# it but only one asserts a new behavior (the incident this case reproduces). Every written rule of the repository is met.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/review.sh"
light_vip_change 2026-11-01-vip-greeting protecting
