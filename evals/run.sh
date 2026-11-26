#!/usr/bin/env bash
# Runs the eval suite against the built Claude Code bundle, on Linux or in WSL (the eval sandbox has no Windows backend).
#
#   evals/run.sh [--no-build] [claude plugin eval options...]
#
# 1. Builds the bundle into evals/.bundle with packaging/package.ps1 (needs pwsh), unless --no-build is given; on
#    Windows, build it there first (`pwsh packaging/package.ps1 -OutputRoot evals/.bundle`) and pass --no-build in WSL.
#    The default package run writes artifacts/ and never touches evals/.bundle.
# 2. Refuses to start without a .NET 10 runtime on PATH: every hook runs `dotnet lib/hoshin.dll`, and a hook that cannot
#    start fails without blocking, so a case could pass its skill-fired grader while nothing ran.
# 3. Runs `claude plugin eval` with TMPDIR outside /tmp (the sandbox denies writes there, and the hooks create folders
#    under TMPDIR), a cost cap (HOSHIN_EVAL_MAX_COST, US dollars, default 30) and results under evals/results/<timestamp>/.
#
# Extra options go to `claude plugin eval`, for example --case <name> --runs 1 --keep-temp.
set -euo pipefail
evals="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$evals/.." && pwd)"

build=1
if [ "${1:-}" = "--no-build" ]; then build=0; shift; fi

if [ "$build" = 1 ]; then
  command -v pwsh >/dev/null || { echo "pwsh is needed to build the bundle; build it elsewhere and pass --no-build" >&2; exit 1; }
  pwsh -NoProfile -File "$root/packaging/package.ps1" -OutputRoot "$evals/.bundle"
fi

bundle="$evals/.bundle/claude-code"
[ -f "$bundle/lib/hoshin.dll" ] || { echo "no bundle at $bundle (lib/hoshin.dll is missing); build it first" >&2; exit 1; }
[ -f "$bundle/hooks/hooks.json" ] || { echo "the bundle at $bundle has no hooks/hooks.json" >&2; exit 1; }

if ! command -v dotnet >/dev/null; then
  echo "dotnet is not on PATH: every hook would fail without blocking, so no case could be trusted" >&2; exit 1
fi
if ! dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft\.NETCore\.App 10\.'; then
  echo "no .NET 10 runtime (dotnet --list-runtimes shows none): the hooks cannot run hoshin.dll" >&2; exit 1
fi
# The runtime must also start the CLI itself, the way a hook does.
dotnet "$bundle/lib/hoshin.dll" scope --json demo >/dev/null 2>&1 || { echo "dotnet cannot run $bundle/lib/hoshin.dll" >&2; exit 1; }

command -v claude >/dev/null || { echo "claude is not on PATH" >&2; exit 1; }
for tool in bwrap socat; do
  command -v "$tool" >/dev/null || { echo "$tool is missing: the eval sandbox needs bubblewrap and socat on Linux" >&2; exit 1; }
done

stamp="$(date -u +%Y-%m-%dT%H-%M-%SZ)"
out="$evals/results/$stamp"
mkdir -p "$out" "$HOME/.evaltmp"
echo "bundle: $bundle"
echo "dotnet: $(dotnet --version) at $(command -v dotnet)"
echo "results: $out"

cd "$evals"
TMPDIR="$HOME/.evaltmp" claude plugin eval . \
  --scaffold --ablation none \
  --allow-tools Bash Write Edit \
  --no-publish --trust-plugin \
  --max-cost-usd "${HOSHIN_EVAL_MAX_COST:-30}" \
  --output-dir "$out" --json "$out/results.json" \
  "$@"
