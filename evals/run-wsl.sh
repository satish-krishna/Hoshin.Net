#!/usr/bin/env bash
# Runs evals/run.sh under WSL, past the machine traps the original plugin's suite met (hoshin/evals/run-wsl.sh):
#   - Docker Desktop's WSL integration leaves symbolic links in ~/.docker, and the eval sandbox refuses to grant Bash
#     while the Docker store holds one. They are moved out for the run and put back on exit.
#   - .NET 10 is often installed per user in WSL (~/.dotnet, by dotnet-install.sh). HOSHIN_DOTNET names that folder
#     (default ~/.dotnet, used when it exists); it is put first on PATH and set as DOTNET_ROOT.
# Build the bundle on Windows first, then run, from Git Bash:
#   pwsh packaging/package.ps1 -OutputRoot evals/.bundle
#   MSYS_NO_PATHCONV=1 wsl.exe -d Ubuntu -- bash -l /mnt/<drive>/<path>/Hoshin.Net/evals/run-wsl.sh --case <name> --runs 1
# Arguments go to run.sh, which is always given --no-build here.
set -u
evals="$(cd "$(dirname "$0")" && pwd)"

d="$HOME/.docker"
bak="$HOME/.docker-evalbak"
moved=()
restore() { for n in "${moved[@]}"; do mv "$bak/$n" "$d/$n"; done; rmdir "$bak" 2>/dev/null; }
trap restore EXIT
if [ -d "$d" ]; then
  mkdir -p "$bak"
  while IFS= read -r link; do
    n="$(basename "$link")"
    mv "$link" "$bak/$n" && moved+=("$n")
  done < <(find "$d" -maxdepth 1 -type l)
fi
echo "moved out of ~/.docker for the run: ${moved[*]:-none}"
if [ -d "$d" ] && [ -n "$(find "$d" -type l)" ]; then echo "~/.docker still holds a link deeper down; the sandbox will refuse Bash:"; find "$d" -type l; exit 1; fi

dotnet_dir="${HOSHIN_DOTNET:-$HOME/.dotnet}"
if [ -d "$dotnet_dir" ]; then
  export PATH="$dotnet_dir:$PATH" DOTNET_ROOT="$dotnet_dir"
fi

bash "$evals/run.sh" --no-build "$@"
