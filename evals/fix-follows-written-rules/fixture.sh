#!/usr/bin/env bash
# The workspace of fix-accepts-a-bug-fix, where the app's contributing guide links to a page that asks every change a
# visitor can see to add a line under Unreleased in CHANGELOG.md. The rule is written down and the code does not show it.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/fix.sh"
(
  cd app
  mkdir -p docs
  cat > CONTRIBUTING.md <<'MD'
# Contributing

- Run the tests with `bash test.sh`.
- Before changing anything a visitor sees, read [docs/changes.md](docs/changes.md).
MD
  cat > docs/changes.md <<'MD'
# Changes a visitor sees

Every change a visitor can see adds a line under `## Unreleased` in `CHANGELOG.md`, in the same pull request.
MD
  cat > CHANGELOG.md <<'MD'
# Changelog

## Unreleased

## 1.0.0

- Returning visitors are greeted by name.
MD
)
buggy_greeting
