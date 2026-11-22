# Hoshin.Net Agent Instructions

This repository contains **Hoshin.Net** (`v2.0.0`): the .NET 10 workflow library (`Hoshin.Workflow`), portable CLI and dual-harness hook driver (`Hoshin.Cli` → `hoshin.dll`), harness-neutral skill procedures (`skills/`), dual-bundle packager (`packaging/`), and conformance test suite (`tests/Hoshin.Tests`).

## Authoritative Documents

- [DESIGN.md](DESIGN.md) — Current system architecture, state machine, Red/Green verification, dual-harness hook contracts, and distribution design.
- [CLIENTS.md](CLIENTS.md) — External CLI contract enforced by `tests/Hoshin.Tests/BlackBox/clients.test.mjs`. Update it whenever adding or changing CLI commands, options, fields, values, or contract messages.
- [docs/KATA_MIGRATION.md](docs/KATA_MIGRATION.md) — Breaking-change guide for Kata Workbench (`crates/kata-sdlc`) and external clients.
- [CHANGELOG.md](CHANGELOG.md) — Release history.

## Architectural Invariants

1. **Minimal dependencies in `Hoshin.Workflow`:** `src/Hoshin.Workflow/Hoshin.Workflow.csproj` may reference **only** `Markdig` and `Gherkin` (`System.Text.Json` handles JSON/JSONC natively). Never add YAML libraries, Claude Code references, Antigravity references, or agent-framework SDKs to `Hoshin.Workflow`.
2. **Harness isolation:** All Claude Code and Google Antigravity payload normalization, response formatting, and hook orchestration live strictly in `src/Hoshin.Cli/Hooks/` and `src/Hoshin.Cli/Commands/`.
3. **SOLID & interface-driven design:** Keep external boundaries behind constructor-injected interfaces (`IJsonSchemaValidator`, `ICommandRunner`, `IGitClient`, `IPullRequestClient`, `IAllowListedCommandRunner`, `IYamlToJsoncConverter`, `IDocumentMigrator`, `IWorkflowRunner`, `IHookAdapter`).
4. **No YAML in Hoshin.Net:** All Hoshin documents use line-1 fenced ```` ```jsonc ```` front matter and ```` ```jsonc ```` record blocks; change records use `change.jsonc`. YAML appears only as input fixtures in migration/negative tests and in generated `SKILL.md` wrapper headers emitted by `packaging/package.ps1`.
5. **Harness-neutral skills:** Files under `skills/*/procedure.md` contain judgment guidance only — zero YAML front matter, zero `hoshin`/`git` CLI invocations, and zero harness-specific tool names (`Agent`, `invoke_subagent`, `Bash`, `run_command`, `PreToolUse`, etc.). This invariant is enforced by `AuthoringAndReviewHookTests.AllSkills_PassStrictLint_NoYamlNoCliNoHarnessToolNames`.
6. **Dogfooding safety:** This repository is developed outside the Hoshin workflow. Do **not** place `.agents/hooks.json` or `.claude-plugin/` in the repository root; keep harness manifests in `packaging/` and generated bundles in `artifacts/` (gitignored) so `hoshin guard` never intercepts development of `Hoshin.Net` itself.

## Build, Test, and Packaging Commands

Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` when running `dotnet` in automation.

```powershell
# Build with zero warnings / zero errors
dotnet build Hoshin.sln

# Run fast C# unit, migration, state-machine, and dual-harness hook tests
dotnet test tests/Hoshin.Tests/Hoshin.Tests.csproj --filter "FullyQualifiedName!~BlackBoxConformanceTests"

# Run the full test suite (all unit tests + all 44 black-box CLI conformance suites)
dotnet test tests/Hoshin.Tests/Hoshin.Tests.csproj

# Package portable CLI, Hoshin.Workflow NuGet package, Claude Code plugin, and Antigravity bundle
powershell -NoProfile -ExecutionPolicy Bypass -File packaging/package.ps1
```

## Git & Pull Request Workflow

- **Never commit or push directly to `main`.** Always branch off the latest `origin/main` using a lowercase kebab-case branch name: `<type>/<short-description>` (where `<type>` is one of `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, `chore`).
- **Pull requests & squash merge only:** Open a PR against `main` with a Conventional Commit title `<type>(<scope>): <subject>` (checked by `.github/workflows/conventional.yml`). All PRs are **squash merged** into `main` once CI passes.

## Coding & Contribution Conventions

- Target `.NET 10` (`net10.0`), `LangVersion=latest`, nullable reference types enabled, `RollForward=Major`.
- Keep all tests green on Windows with zero build warnings before committing.
- Hooks (`hoshin hook ...` and `hoshin guard`) must **always** exit `0`, write errors to `stderr`, read `stdin` and write `stdout` as raw UTF-8 streams (avoiding Windows OEM codepage corruption), and keep injected text under 10,000 characters.
- Avoid embedded double quotes in Antigravity hook commands (`packaging/antigravity/hooks.json`) for Windows `cmd /c` compatibility.
