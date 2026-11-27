#!/usr/bin/env bash
# The base workspace with the greeting bug in place: a named returning visitor is greeted "Welcome back,Mina", which
# breaks context rule C1. No change has started.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/fix.sh"
buggy_greeting
