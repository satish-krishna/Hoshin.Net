#!/usr/bin/env bash
# A context file that is two commits behind app, with the history a refresh must read:
#
#   v0, built at app A0:   C1 to C5 (C5, a coupon rule, existed)
#   v1, refreshed at A1:   C1 to C4  (C5 retired, so the next free number is C6, never C5)
#   app A2 and A3 since:   first-time visitors now get a "Welcome" greeting (C2 no longer holds), a farewell is new,
#                          and the weekend banner is gone (C4's code no longer exists)
#
# C1 and C3 still hold, so a refresh carries them over unchanged with their tags and notes. The engineer is away, so no
# changed rule can be confirmed. Both context files are committed through the program, as a context run's Stop commits
# them, so the master's history shows a build and a refresh before the run.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/context.sh"

init_app
(
  cd app
  cat > greet.sh <<'SH'
#!/usr/bin/env bash
# Prints the greeting for a visitor: greet.sh <returning|new> [first name]
[ "${1:-}" = "returning" ] || exit 0
echo "Welcome back${2:+, $2}"
SH
  cat > banner.sh <<'SH'
#!/usr/bin/env bash
# Prints the banner every visitor sees on a weekend.
[ "$(date +%u)" -ge 6 ] && echo "Weekend sale"
exit 0
SH
  cat > coupon.sh <<'SH'
#!/usr/bin/env bash
# Prints the discount a visitor with a coupon gets.
echo "10 percent off"
SH
)
app_commit "2026-09-01T09:00:00+0000" "feat: greet visitors, weekend banner and coupons"
a0=$(app_head)

init_master
cat > master/intent/demo/context/app.md <<MD
\`\`\`jsonc
{ "repo": "app", "system": "demo", "source_commit": "$a0", "built": "2026-09-01", "built_by": "Eve Engineer" }
\`\`\`

# Context: app

The app greets visitors, shows a weekend banner and gives coupon holders a discount.

## Rules the system enforces

| # | Rule | Provenance | Note |
|---|---|---|---|
| C1 | A returning visitor who gave a first name sees "Welcome back, " followed by that name | confirmed by engineer | Eve confirmed on the 2026-09-02 call |
| C2 | A first-time visitor sees no greeting | confirmed by engineer | Eve confirmed on the 2026-09-02 call |
| C3 | A returning visitor who gave no first name sees "Welcome back" | intent unknown | Ask Pat in product whether a missing name should show a different greeting |
| C4 | On weekends every visitor sees a "Weekend sale" banner | seen in code | |
| C5 | A visitor with a coupon sees "10 percent off" | seen in code | |
MD
commit_context intent/demo/context/app.md

(cd app && git rm -q coupon.sh)
app_commit "2026-10-01T09:00:00+0000" "feat: retire coupons"
a1=$(app_head)
sed -e "s/\"source_commit\": \"$a0\"/\"source_commit\": \"$a1\"/" -e 's/"built": "2026-09-01"/"built": "2026-10-01"/' -e '/^| C5 |/d' \
  -e 's/, shows a weekend banner and gives coupon holders a discount\./ and shows a weekend banner./' \
  master/intent/demo/context/app.md > master/intent/demo/context/app.md.new
mv master/intent/demo/context/app.md.new master/intent/demo/context/app.md
commit_context intent/demo/context/app.md

(
  cd app
  cat > greet.sh <<'SH'
#!/usr/bin/env bash
# Prints the greeting for a visitor: greet.sh <returning|new> [first name]
if [ "${1:-}" != "returning" ]; then echo "Welcome"; exit 0; fi
echo "Welcome back${2:+, $2}"
SH
)
app_commit "2026-10-15T09:00:00+0000" "feat: greet first-time visitors"
(
  cd app
  git rm -q banner.sh
  cat > farewell.sh <<'SH'
#!/usr/bin/env bash
# Prints the farewell a returning visitor sees when they leave.
echo "See you soon"
SH
)
app_commit "2026-10-25T09:00:00+0000" "feat: say goodbye, drop the banner"
