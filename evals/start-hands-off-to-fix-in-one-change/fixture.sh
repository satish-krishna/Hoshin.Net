#!/usr/bin/env bash
# The base workspace with the original suite's greeting bug (shared/start.sh): context rule C1 says a returning visitor
# sees "Welcome back, " and the name, and greet.sh prints no space after the comma.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/start.sh"
