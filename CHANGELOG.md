# Changelog

All notable changes to **Hoshin.Net** are documented in this file.

## [Unreleased]

### Changed
- **Cross-platform packager (`packaging/package.ps1`):** Uses `Join-Path` throughout, runs under `pwsh` on Linux and Windows, reads default version from `Directory.Build.props` (`VersionPrefix`), and stamps `-Version` into the generated Claude Code `plugin.json`.
- **Strict analyzer & complexity build gate:** Enabled `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, `.editorconfig`, and `CA1502` (cyclomatic complexity ≤ 15 via `CodeMetricsConfig.txt`); replaced per-call hot-path regexes with `[GeneratedRegex]`.
- **Architecture & SOLID test suite (`tests/Hoshin.Tests/Architecture/`):** Automated checks for rules A1, A2, S2, S3, CA1502, I1, I2, D1–D4, and C3 with a shrinking `baseline.json`.
- **Conventional names workflow (`.github/workflows/conventional.yml`):** Validates PR title, branch name, every commit header, and unique `Task: <id>` trailers from `docs/review/TASKS.md` on `fix/review-findings`.
- **Changed-line coverage gate (`.github/workflows/ci.yml`):** Collects full-suite coverage (including child `dotnet hoshin.dll` black-box processes) with `dotnet-coverage`, enforces ≥ 90% changed-line coverage on `src/**` via `diff-cover`, and uploads the HTML report artifact.
- **Active-run guard & trace scoping (T01):** `hoshin guard` and `hooks.jsonl` tracing now apply strictly to repositories with an active, non-stale `.git/hoshin/run.json` matching the calling session (or its active subagent), stay silent without creating `.git/hoshin/` outside active runs, and log static denials when active.
- **Human-only approval and acceptance (T02):** Generated `SKILL.md` wrappers for `accept` and `done` emit `disable-model-invocation: true`, and `HookOrchestrator` rejects model-sourced `accept`/`done` invocations (`PostToolUse(Skill)` or `SKILL.md` reads) without changing state.
- **Harness config & hoshin state protection (T03):** `GuardTamperingProtection` denies writes to `.claude/settings*.json`, `.claude/hooks/`, `.git/hooks/`, `<git-common-dir>/hoshin`, and shell write commands targeting git/hoshin state tokens; `HookOrchestrator.HandleStop` blocks if `run.json` is deleted mid-run while an unfinished ledger exists.
- **Guard bypass hardening & library HEAD enforcement (T04):** Normalized shell commands (stripping empty quotes and line continuations), extended `GitPrefix` (`--git-dir`, `--work-tree`, `--namespace`, `-C`), blocked inline/configured git aliases and `git commit ... -n`, matched `>`/`>>`/`|` prefixes on protected paths, and enforced `WorkflowHeadGuard` in `EvaluateAndCommitTask` so manual commits outside the workflow fail the attempt without committing.
- **Base-branch build checks (T05):** `BaseBranchChecksReader` loads blocking `build.finish` checks exclusively from the base ref via `git show <base>:...` (combining `.hoshin.json`, `intent/<system>/hooks.json` via `HookConfigService`, and any committed per-change `hooks.jsonc`/`hooks.json`, with a uniform `blocking` default of `true`), preventing working-tree tampering.
- **Change-folder & shell-redirect protection (T06):** Protected the active change folder during `Implement`/`Retry` in both `WorkflowRunner.IsProtectedPath` and `GuardCommand`, and added `ShellWriteTargets` to enforce Red-phase and single-document write locks on shell `>`, `>>`, and `tee` targets.
- **Per-change workflow ledger (T07):** Scoped the workflow ledger to `.git/hoshin/changes/<change>/workflow-ledger.json` via `ChangeLedgerPaths` so task IDs across different changes never collide, with one-time migration of legacy `.git/hoshin/workflow-ledger.json` when `run.json` names a change.
- **Task path isolation & NUL-delimited status parsing (T08):** `WorkingTreeSnapshot` captures pre-issue dirty/untracked file digests and staged-index state on `WorkflowIssued`, parses `git status --porcelain=v1 -z -uall`, refuses attempts when the index had staged changes before issue, excludes unchanged pre-issue files, commits with explicit `-- <paths>` pathspecs, and resets `--mixed` to `startHead` on any failure after staging begins.
- **Serialized task evaluation and commit (T09):** Added `FileLock` and a run-level lock (`.git/hoshin/run.lock`) held across `WorkflowRunner.EvaluateAndCommitTask`, re-reading the live ledger inside the lock to return any existing verdict before touching the working tree, worktree, or git history.
- **Fail-closed YAML migration (T10):** Hardened `YamlToJsoncConverter` and `DocumentMigrator` via `MigrationGuard` to refuse unconsumed lines, anchors/aliases/tags, multi-line quoted scalars, block scalars inside sequences, nested sequences, duplicate keys, leading `---` in `change.yml`, and partial flow collections; fixed quote-aware inline comment stripping (`SplitInlineCommentSafe`); and verified converted JSONC retains every source key before rewriting or deleting `change.yml`.
- **Blank & comment-only JSONC handling (T12):** Fixed `Jsonc.IsBlank` so empty, whitespace-only, and comment-only (`// ...`, `/* ... */`) JSONC inputs return `true` with `value = null` as documented (while unclosed `/*` still fails), allowing comment-only `## Shared decisions` blocks in plans.

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
