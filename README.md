# Hoshin.Net

A .NET 10 workflow library and portable CLI (`hoshin.dll`) for hoshin's JSONC document format, lifecycle events, baseline checks, observed red/green verification, and dual-harness (Claude Code & Google Antigravity) workflow hooks.

## Solution Layout

- `src/Hoshin.Workflow` — Core workflow library (`Markdig` + `Gherkin` only; zero harness or agent-framework dependencies).
- `src/Hoshin.Cli` — Portable CLI entry point producing `hoshin.dll` (`dotnet hoshin.dll <command> ...`).
- `tests/Hoshin.Tests` — Unit tests and black-box CLI conformance test suite.

## Building & Testing

```powershell
dotnet build
dotnet test
```
