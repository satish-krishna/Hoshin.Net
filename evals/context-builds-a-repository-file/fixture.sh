#!/usr/bin/env bash
# A master with no context file for app, and an app whose code is clear except for one thing: the cart tells a shopper
# shipping is free from 50.00, and checkout charges it up to 42.17. Two code paths contradict each other, which is the
# first trigger in the context skill's "when to interview the engineer". The greeting rules are clear.
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
  cat > cart.sh <<'SH'
#!/usr/bin/env bash
# Shows the shipping estimate in the cart: cart.sh <order total in cents>
if [ "$1" -ge 5000 ]; then echo "Shipping: free"; else echo "Shipping: 4.99"; fi
SH
  cat > checkout.sh <<'SH'
#!/usr/bin/env bash
# Charges shipping at checkout: checkout.sh <order total in cents>
if [ "$1" -gt 4217 ]; then echo "shipping=0"; else echo "shipping=499"; fi
SH
)
app_commit "2026-10-01T09:00:00+0000" "feat: greet visitors and ship orders"

init_master
