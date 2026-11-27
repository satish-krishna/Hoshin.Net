#!/usr/bin/env bash
# The drafted fix of fix-redrafts-from-feedback, in a system whose workspace also holds a second repository, web, with
# its own context file. Registering web is the last commit on the master's main, and the draft the last in the change's
# copy, so a run that commits nothing leaves both reflogs as the fixture left them.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/fix.sh"
buggy_greeting
add_web
draft_fix
