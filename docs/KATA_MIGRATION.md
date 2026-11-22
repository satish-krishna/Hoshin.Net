# Kata Workbench Migration Guide: Hoshin v2.0.0 (`Hoshin.Net`)

**Release:** `2.0.0` (breaking major cut retiring `tools/hoshin.ts`)  
**Audience:** Maintainers of `satish-krishna/kata` (`crates/kata-sdlc`) and any other external CLI client of `hoshin`.

Hoshin v2.0.0 replaces the TypeScript CLI (`node <plugin>/tools/hoshin.ts`) with the portable .NET 10 CLI (`dotnet <plugin>/lib/hoshin.dll`) and standardizes all change documents and records on **Layout 2 JSONC**. Every JSON output contract, exit code, streaming event shape, and message prefix documented in [CLIENTS.md](../CLIENTS.md) is preserved.

---

## Summary of Breaking Changes

| Area | Hoshin v1.x (TypeScript) | Hoshin v2.0.0 (`Hoshin.Net`) | Action Required in Kata |
| :--- | :--- | :--- | :--- |
| **CLI invocation** | `node <plugin>/tools/hoshin.ts <cmd> ...` | `dotnet <plugin>/lib/hoshin.dll <cmd> ...` | Replace `node` + `tools/hoshin.ts` launcher with `dotnet` + `lib/hoshin.dll` (requires .NET 10+ runtime; `RollForward=Major` enabled). |
| **Front matter format** | YAML delimited by `---` on line 1 | JSONC inside a fenced ```` ```jsonc ```` block on line 1 | Update Kata's direct front-matter reader/writer (`status`, `scope`, `source_commit`, `source_intent_commit`, `source_spec_commit`). |
| **Change record file** | `intent/<system>/<change>/change.yml` | `intent/<system>/<change>/change.jsonc` | Read `change.jsonc` (JSON with `//` comments and trailing commas) instead of `change.yml`. |
| **Document layouts** | Layouts 1 and 2 (YAML record blocks) | Layout 2 JSONC only | Run `dotnet hoshin.dll migrate <master> --json` (or `hoshin migrate --json --write`) once per legacy repository; refuse Layout 1 until migrated. |
| **Record blocks** | ```` ```yaml ```` blocks inside `plan.md`, `build/*.md`, `build/review.md` | ```` ```jsonc ```` blocks | Update any Kata-side conformance fixture or parser reading record blocks directly. |
| **Plugin layout** | Claude Code only (`.claude-plugin/`) | Dual-harness: `.claude-plugin/` (Claude Code) and `.agents/` (Antigravity) | Detect `lib/hoshin.dll` alongside `skills/build/SKILL.md` and `.claude-plugin/plugin.json` (`version >= 2.0.0`). |

---

## 1. Switching Subprocess Invocation (`hoshin.ts` → `hoshin.dll`)

Replace:

```text
node <plugin>/tools/hoshin.ts <command> [options...]
```

With:

```text
dotnet <plugin>/lib/hoshin.dll <command> [options...]
```

### Runtime & Environment Notes
- **Runtime requirement:** .NET 10 Runtime (`dotnet`). `hoshin.runtimeconfig.json` sets `rollForward: "Major"`, so newer .NET runtimes work without rebuilding.
- **No Node.js dependency:** Node.js is no longer required to invoke `hoshin`.
- **Stdout / Exit Codes (unchanged):**
  - The final non-empty line of `stdout` is one JSON object when `--json` is passed.
  - `--stream` emits newline-delimited JSON objects (`hook-start`, `hook-end`, final `outcome`).
  - Exit `0`: success; Exit `1`: refusal or validation problems (`problems` / `reason`); Exit `2`: could not run (`problems` or `stderr`).
  - Pass `DOTNET_CLI_TELEMETRY_OPTOUT=1` if desired in headless/CI environments.

---

## 2. Updating Front-Matter Reading & Writing in Kata

Kata reads and updates a few front-matter fields directly without invoking `hoshin`:
- `intent.md`, `spec.md`, `plan.md`: reads `status`, `scope`, `source_intent_commit`, `source_spec_commit`; writes `status` (`"accepted"` / `"rejected"`) before calling `hoshin commit`.
- `intent/<system>/context/<repo>.md`: reads `source_commit`.

### Old Format (YAML — rejected in v2.0.0)
```yaml
---
layout: 2
system: kata
status: draft
---
```

### New Format (Canonical Layout 2 JSONC)
````markdown
```jsonc
{
  "layout": 2,
  "system": "kata",
  "status": "draft"
}
```
````

### Implementation Guidance for `crates/kata-sdlc`
1. **Reading:** Look for a line-1 fenced block opening with ```` ```jsonc ```` (or ```` ```json ````) and closing on the next ```` ``` ```` line. Strip `//` line comments and `/* ... */` block comments (and allow trailing commas), then deserialize with `serde_json`.
2. **Writing `status`:** Replace `"status": "<old>"` in place inside the line-1 ```` ```jsonc ```` block (preserving all other keys, comments, and line numbers so diff-based acceptance checks stay clean), then commit via `dotnet <plugin>/lib/hoshin.dll commit --json --kind accept-<doc> ...`.
3. **Detection of unmigrated repos:** If line 1 is `---` or `change.yml` exists without `change.jsonc`, prompt the engineer to run `hoshin migrate`.

---

## 3. Reading `change.jsonc` Instead of `change.yml`

`change.jsonc` has the exact same schema and field names as `change.yml`, serialized as JSONC with a leading `//` comment:

```jsonc
// Written by the hoshin program (change start, change rename, change.done); do not edit by hand.
{
  "version": 1,
  "branch": "feat/example",
  "type": "feat",
  "scope": "kata",
  "base": "main",
  "pullRequests": [
    {
      "repo": "kata",
      "url": "https://github.com/org/kata/pull/12",
      "number": 12,
      "pushed": "abc1234"
    }
  ]
}
```

- All-digit commit SHAs (e.g., `"0123456"`) are now always JSON strings—no YAML failsafe workarounds needed.
- Prefer calling `dotnet <plugin>/lib/hoshin.dll change show --json --change <change>` where possible, as its JSON contract is unchanged.

---

## 4. Migrating Existing Repositories

Existing repositories with Layout 1 or Layout 2 YAML documents and `change.yml` migrate in place using `hoshin migrate`:

```powershell
# Preview changes without modifying any file:
dotnet <plugin>/lib/hoshin.dll migrate <master-repo> --dry-run --json

# Apply migration (idempotent; preserves comments and key order):
dotnet <plugin>/lib/hoshin.dll migrate <master-repo> --json

# Or migrate an entire legacy ai-sdlc/hoshin workspace (markers, copies, state, instructions, and YAML documents):
dotnet <plugin>/lib/hoshin.dll migrate --json --write
```

After migration, verify cleanly with:

```powershell
dotnet <plugin>/lib/hoshin.dll check <change>/spec.md <change>/plan.md <change>/build/<repo>.md <change>/change.jsonc
```

---

## 5. Unchanged Contracts

All 22 CLI subcommands (`resolve`, `state`, `list`, `stale`, `instructions`, `hooks`, `status`, `run`, `change start|show|rename|remove`, `commit`, `read`, `red show|record`, `migrate`, `checks`, `baseline show|approve|retake`, `check`, `naming`, `branch-name`, `scope`, `fix-state`, `gate`, `guard`) preserve their exact option names, JSON field names, enum values, exit codes, and error message prefixes verified by `tests/Hoshin.Tests/BlackBox/clients.test.mjs` and the 44 black-box conformance suites.
