#!/usr/bin/env bash
# The built change of review-commits-findings, plus a review that wrote its section and stopped before it was committed:
# build/review.md is untracked in the change's copy and holds only Review 1.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/review.sh"
change=2026-11-01-vip-greeting
vip_greeting_change "$change" observed

head="$(git -C app rev-parse --short=7 HEAD)"
cat > "$(copy_of "$change")/$(change_rel "$change")/build/review.md" <<MD
\`\`\`jsonc
{ "layout": 2 }
\`\`\`

# Review findings

## Review 1

\`\`\`jsonc
{ "review": 1,
  "reviewed_at": "2026-11-26",
  "commits": { "app": "$head" } }
\`\`\`

### F1

\`\`\`jsonc
{ "id": "F1",
  "severity": "low",
  "repo": "app",
  "concern": "bug",
  "file": "greet.sh",
  "line": 4 }
\`\`\`

An interrupted draft: a name with surrounding spaces is echoed as given.
MD
out="$(cd master && hoshin state --json --system demo --change "$change")"
case "$out" in
  *'"review":{"committed":0,"draft":"none"'*) echo "fixture: the review draft is not seen as a draft: $out" >&2; exit 1 ;;
esac
