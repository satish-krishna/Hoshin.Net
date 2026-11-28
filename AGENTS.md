# Hoshin.Net Agent Instructions

This repository contains **Hoshin.Net** (`v2.0.0`): the .NET 10 workflow library with its harness-neutral driver (`Hoshin.Workflow`), the Microsoft Agent Framework host (`Hoshin.Agents`), the portable CLI and Claude Code / Antigravity hook adapters (`Hoshin.Cli` → `hoshin.dll`), harness-neutral skill procedures (`skills/`), the packager (`packaging/`), and the test suite (`tests/Hoshin.Tests`).

**Before any change, read [CONTRIBUTING.md](CONTRIBUTING.md) and follow it.** Its rules (Conventional Commits, test first, the SOLID design rules in §3, the code rules in §4, the coverage gate, and the document owners) are mandatory for agents and people alike. A change that breaks one is not merged. When working from [docs/review/TASKS.md](docs/review/TASKS.md), also follow the instructions at the top of that file.

## Authoritative Documents

- [CONTRIBUTING.md](CONTRIBUTING.md) — How to change Hoshin.Net: branches, commits, pull requests, design rules, code rules, tests, releases, and which document owns what.
- [DESIGN.md](DESIGN.md) — Current system architecture, state machine, Red/Green verification, dual-harness hook contracts, and distribution design.
- [CLIENTS.md](CLIENTS.md) — External CLI contract enforced by `tests/Hoshin.Tests/BlackBox/clients.test.mjs`. Update it whenever adding or changing CLI commands, options, fields, values, or contract messages.
- [docs/KATA_MIGRATION.md](docs/KATA_MIGRATION.md) — Breaking-change guide for Kata Workbench (`crates/kata-sdlc`) and external clients.
- [CHANGELOG.md](CHANGELOG.md) — Release history.

## Architectural Invariants

1. **Minimal dependencies in `Hoshin.Workflow`:** `src/Hoshin.Workflow/Hoshin.Workflow.csproj` may reference **only** `Markdig` and `Gherkin` (`System.Text.Json` handles JSON/JSONC natively). Never add YAML libraries, Claude Code references, Antigravity references, or agent-framework SDKs to `Hoshin.Workflow`.
2. **Harness isolation:** All Claude Code and Google Antigravity payload normalization, response formatting, and hook orchestration live strictly in `src/Hoshin.Cli/Hooks/` and `src/Hoshin.Cli/Commands/`. Microsoft Agent Framework code lives only in `src/Hoshin.Agents/`. Every host advances a change through the driver's `StepAdvancer`, `WorkSubmitter` and `DecisionApplier` (`src/Hoshin.Workflow/Driver/`); a host never commits, judges or gates work itself.
3. **SOLID design is mandatory:** Follow [CONTRIBUTING.md §3](CONTRIBUTING.md#3-design-rules-solid). In short:
   - decisions are pure;
   - every outside effect goes through a required, constructor-injected interface, with no `?? Default` / `?? new` fallbacks;
   - `HoshinServices` is the only composition root;
   - interfaces are small and role-shaped;
   - closed sets are enums or records, not strings;
   - each rule has one owner.

   Existing violations are baselined, and a change may not add one.
4. **No YAML in Hoshin.Net:** All Hoshin documents use line-1 fenced ```` ```jsonc ```` front matter and ```` ```jsonc ```` record blocks; change records use `change.jsonc`. YAML appears only as input fixtures in negative tests and in generated `SKILL.md` wrapper headers emitted by `packaging/package.ps1`.
5. **Harness-neutral skills:** Files under `skills/*/procedure.md` contain judgment guidance only — zero YAML front matter, zero `hoshin`/`git` CLI invocations, and zero harness-specific tool names (`Agent`, `invoke_subagent`, `Bash`, `run_command`, `PreToolUse`, etc.). This invariant is enforced by `AuthoringAndReviewHookTests.AllSkills_PassStrictLint_NoYamlNoCliNoHarnessToolNames`.
6. **Dogfooding safety:** This repository is developed outside the Hoshin workflow. Do **not** place `.agents/hooks.json` or `.claude-plugin/` in the repository root; keep harness manifests in `packaging/` and generated bundles in `artifacts/` (gitignored) so `hoshin guard` never intercepts development of `Hoshin.Net` itself.

## Build, Test, and Packaging Commands

Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` when running `dotnet` in automation.

```powershell
# Build with zero warnings / zero errors
dotnet build Hoshin.sln

# Run fast C# unit, state-machine, and dual-harness hook tests
dotnet test tests/Hoshin.Tests/Hoshin.Tests.csproj --filter "FullyQualifiedName!~BlackBoxConformanceTests"

# Run the full test suite (all unit tests + all 44 black-box CLI conformance suites)
dotnet test tests/Hoshin.Tests/Hoshin.Tests.csproj

# Package portable CLI, Hoshin.Workflow and Hoshin.Agents NuGet packages, Claude Code plugin (with a local marketplace), and Antigravity bundle
powershell -NoProfile -ExecutionPolicy Bypass -File packaging/package.ps1
```

## Git & Pull Request Workflow

- **Never commit or push directly to `main`.** Always branch off the latest `origin/main` using a lowercase kebab-case branch name: `<type>/<short-description>` (where `<type>` is one of `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, `chore`).
- **Pull requests & squash merge only:** Open a PR against `main` with a Conventional Commit title `<type>(<scope>): <subject>` (checked by `.github/workflows/conventional.yml`). All PRs are **squash merged** into `main` once CI passes.

## Coding & Contribution Conventions

The full rules are in [CONTRIBUTING.md](CONTRIBUTING.md). These are the harness-specific reminders.

- Target `.NET 10` (`net10.0`), `LangVersion=latest`, nullable reference types enabled, `RollForward=Major`.
- Keep all tests green on Linux and Windows with zero build warnings before committing. Never skip, filter out or weaken a test to get green.
- Hooks (`hoshin hook ...` and `hoshin guard`) must **always** exit `0`, write errors to `stderr`, read `stdin` and write `stdout` as raw UTF-8 streams (avoiding Windows OEM codepage corruption), and keep injected text under 10,000 characters.
- Avoid embedded double quotes in Antigravity hook commands (`packaging/antigravity/hooks.json`) for Windows `cmd /c` compatibility.
