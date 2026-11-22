# Hoshin.Net

A .NET 10 workflow library and portable CLI (`hoshin.dll`) for Hoshin's Layout 2 JSONC document format, lifecycle events, baseline checks, strongly-typed detached worktree Red/Green verification, and dual-harness (**Claude Code** & **Google Antigravity**) workflow hooks.

## Solution Layout

- `src/Hoshin.Workflow` — Core workflow library (`Markdig` + `Gherkin` only; zero Claude Code, Antigravity, or agent-framework dependencies) with embedded schemas, templates, and harness-neutral role procedures.
- `src/Hoshin.Cli` — Portable CLI entry point producing `hoshin.dll` (`dotnet hoshin.dll <command> ...`), including all `CLIENTS.md` commands, `hoshin migrate`, `hoshin hook`, and `hoshin guard`.
- `skills/` — Harness-neutral, judgment-only procedures (`procedure.md`) for all 10 skills (`intent`, `spec`, `plan`, `build`, `review`, `fix`, `done`, `accept`, `setup`, `context`) plus the `implement` subagent role.
- `packaging/` — Release manifests and `package.ps1` generating the portable CLI bundle, `Hoshin.Workflow` NuGet package, Claude Code plugin bundle (`.claude-plugin/`), and Antigravity bundle (`.agents/`).
- `tests/Hoshin.Tests` — Unit tests, `WorkflowEngine.Next` state-machine tests, migration tests, dual-harness hook integration tests, and 44 black-box CLI conformance suites.
- `CLIENTS.md` & `docs/KATA_MIGRATION.md` — External client contract and Kata Workbench v2.0.0 migration guide.

## Building, Testing & Packaging

```powershell
# Build solution
dotnet build Hoshin.sln

# Run all unit, migration, hook integration, and black-box conformance tests
dotnet test tests/Hoshin.Tests/Hoshin.Tests.csproj

# Package portable CLI, NuGet package, Claude Code plugin, and Antigravity bundle
powershell -NoProfile -ExecutionPolicy Bypass -File packaging/package.ps1
```

## Migrating Legacy YAML Repositories

```powershell
# Preview migration (dry run)
dotnet hoshin.dll migrate <path-to-repo> --dry-run --json

# Rewrite Layout 1 / Layout 2 YAML documents and change.yml to Layout 2 JSONC & change.jsonc
dotnet hoshin.dll migrate <path-to-repo> --json
```
