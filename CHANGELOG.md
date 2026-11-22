# Changelog

All notable changes to **Hoshin.Net** are documented in this file.

## [Unreleased]

### Changed
- **Cross-platform packager (`packaging/package.ps1`):** Uses `Join-Path` throughout, runs under `pwsh` on Linux and Windows, reads default version from `Directory.Build.props` (`VersionPrefix`), and stamps `-Version` into the generated Claude Code `plugin.json`.
- **Strict analyzer & complexity build gate:** Enabled `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, `.editorconfig`, and `CA1502` (cyclomatic complexity ≤ 15 via `CodeMetricsConfig.txt`); replaced per-call hot-path regexes with `[GeneratedRegex]`.
- **Architecture & SOLID test suite (`tests/Hoshin.Tests/Architecture/`):** Automated checks for rules A1, A2, S2, S3, CA1502, I1, I2, D1–D4, and C3 with a shrinking `baseline.json`.

## [2.0.0] - 2026-11-21

### Breaking Changes (`!`)
- **Retired `tools/hoshin.ts` in favor of `dotnet hoshin.dll` (.NET 10):** All CLI commands, lifecycle events, and hooks now run through the portable .NET 10 build (`hoshin.dll`, `Hoshin.Workflow.dll`, `Markdig.dll`, `Gherkin.dll`) with `RollForward=Major`.
- **JSONC replaces YAML in all Hoshin documents and records:**
  - Front matter must be a fenced ```` ```jsonc ```` block starting on line 1 (bare line-1 JSONC written during authoring is auto-wrapped before commit; YAML `---` front matter is refused with migration guidance).
  - Record blocks in `plan.md`, `build/*.md`, and `build/review.md` use ```` ```jsonc ````.
  - `change.yml` is replaced by `change.jsonc`.
  - Layout 1 readers are removed from `Hoshin.Workflow`; migrate legacy repositories with `hoshin migrate`.
- **Kata Workbench migration:** See [docs/KATA_MIGRATION.md](docs/KATA_MIGRATION.md) and [CLIENTS.md](CLIENTS.md).

### Added
- **`Hoshin.Workflow` NuGet package (`2.0.0`):** Minimal dependencies (`Markdig` + `Gherkin` only), zero harness references, embedded JSON schemas, workspace templates, and all 11 harness-neutral role/skill procedures.
- **Document & record migration (`hoshin migrate`):** Zero-dependency YAML-to-JSONC converter and idempotent migrator for Layout 1 & Layout 2 Markdown documents and `change.yml` → `change.jsonc`, supporting `--dry-run`, `--write`, and `--documents`.
- **Pure workflow state machine (`WorkflowEngine.Next`) & two-phase Red/Green runner:** Full lifecycle (`WriteIntent` → `AwaitAccept(intent)` → `WriteSpec` → `AwaitAccept(spec)` → `WritePlan` → `AwaitAccept(plan)` → `Implement`/`Retry` → `RunChecks` → `Review` → `NeedsApproval` → `Publish` → `Done`), cached detached worktree Red verification with strongly-typed missing-symbol classification (`CS0246`, `CS0103`, `CS1061`, `TS2304`, ESM missing exports), atomic test-then-implementation commits, and allow-listed secret-free command execution.
- **First-class dual-harness hooks (`hoshin hook` + `hoshin guard`):** Native payload normalization and response formatting for both **Claude Code** and **Google Antigravity**, supporting foreground and async background subagents, session-aware fast-path guarding, and authoring/review/fix Stop gates.
- **Dual-bundle release packager (`packaging/package.ps1`):** Produces `artifacts/portable/` (`lib/` + `skills/`), `artifacts/nuget/Hoshin.Workflow.2.0.0.nupkg`, `artifacts/claude-code/` (`.claude-plugin/`), and `artifacts/antigravity/.agents/`.
