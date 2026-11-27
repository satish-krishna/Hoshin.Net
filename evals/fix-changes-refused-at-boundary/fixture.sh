#!/usr/bin/env bash
# The drafted fix of fix-feedback-refused-at-boundary after the engineer's first round of feedback: the fix was redrafted
# with one more scenario (a hyphenated name) and committed again as "draft fix". The engineer's answer at the
# acceptance question is the change the case is about, so the last two commits in the change's copy are the drafts.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/fix.sh"
buggy_greeting
add_web
draft_fix
redraft_with_hyphen
