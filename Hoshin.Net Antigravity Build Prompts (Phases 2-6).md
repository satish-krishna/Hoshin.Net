# Hoshin.Net Antigravity Build Prompts (Phases 2–6)

Since **Phase 1 (Spikes 1–5)** is complete, use these prompts sequentially in a brand-new `Hoshin.Net` repository inside Google Antigravity (`agy`). Each phase stops at an explicit verification gate before moving to the next prompt.

> **Before running Phase 2:** Place a copy of the updated design document (`Hoshin.Net: a .NET workflow driven by harness hooks.md`) in the root (or `docs/`) of your brand-new `Hoshin.Net` repository, and verify the local paths to your existing `hoshin` TypeScript repo and `experiments` spike branch (`feat/hoshin-net-spikes`).

---

## Prompt 1: Phase 2 — New Repository Scaffold, Readers, CLI, and Conformance Suite

```
We are initializing and building **Hoshin.Net** in this brand-new repository, following `Hoshin.Net: a .NET workflow driven by harness hooks.md`.

### Context & Reference Paths
Phase 1 (Spikes 1–5) is complete. Before writing code, read:
1. `Hoshin.Net: a .NET workflow driven by harness hooks.md` (in this repository — our architecture, invariants, and rollout plan).
2. `D:/Repos/experiments/hoshin-net/findings.md` and the spike prototypes on branch `feat/hoshin-net-spikes` (specifically the Markdig JSONC readers, Gherkin spec validator, detached worktree Red/Green runner, and Spike 5 Antigravity/Claude Code `HarnessAdapter`).
3. `D:/workspaces/hoshin/` (our existing TypeScript implementation):
   - `tools/GRAMMAR.md` and `tools/CLIENTS.md` (the exact CLI commands, options, JSON output contracts, and refusal rules we must preserve).
   - `tools/documents/`, `tools/schema/*.json`, and `tools/test/` (the ~50 black-box test files that serve as our conformance suite).
   - `references/workspace.md` (domain rules for change documents, pins, and guards).

### Architectural Invariants
- **Brand-New Repo Hygiene:** Initialize `.gitignore` for .NET 10, `global.json` (pinning .NET 10 SDK), `Directory.Build.props` (`net10.0`, C# latest, nullable enabled, `RollForward` set to `Major`), and `README.md`.
- **Dogfooding Safety:** Do **not** place `.agents/hooks.json` or `.claude-plugin/` in the root of this `Hoshin.Net` repository while we are developing it, so `hoshin guard` never intercepts our own coding agents. Keep harness manifests inside test fixtures or a `packaging/` folder until release.
- **Minimal Dependencies:** `Hoshin.Workflow` references **only** `Markdig` and `Gherkin` (`System.Text.Json` handles JSON and JSONC natively). Zero references to Claude Code, Antigravity, or Microsoft Agent Framework inside `Hoshin.Workflow`.
- **No YAML in Hoshin.Net:** All structured blocks in Markdown documents (front matter on line 1, task fields, build-log entries, review findings) and `change.jsonc` are JSONC.

### Phase 2 Tasks
1. **Scaffold the Solution (`Hoshin.sln`):**
   - `src/Hoshin.Workflow` (core library)
   - `src/Hoshin.Cli` (portable CLI entry point producing `hoshin.dll`)
   - `tests/Hoshin.Tests` (unit and black-box conformance test suite)
2. **Port Document Readers & Validators (`Hoshin.Workflow`):**
   - Port the `intent`, `spec` (using Cucumber's `Gherkin` package to enforce `@R<n>`, `@from:`, `@R<n>.<m>`, outline parameters, and tag placement rules), Layout 2 `plan`, `build-log`, `review`, and `change.jsonc` readers on `Markdig` and `System.Text.Json`.
   - Port the JSON schema validators so validation diagnostics match `tools/GRAMMAR.md`.
3. **Port CLI Commands (`Hoshin.Cli`):**
   - Implement all commands, flags, JSON outputs, and exit/refusal semantics defined in `tools/GRAMMAR.md` (excluding `Next`/hook commands, which land in Phase 4).
4. **Port the Black-Box Conformance Suite (`tests/Hoshin.Tests`):**
   - Port the ~50 black-box test files from `D:/workspaces/hoshin/tools/test/`, converting their YAML fixtures to Layout 2 JSONC and pointing the test runner at `dotnet hoshin.dll`.

### Phase 2 Exit Gate
- First, propose the `Hoshin.sln` folder/namespace structure and confirm before generating the files.
- Do not proceed to Phase 3 until `dotnet build` succeeds cleanly and 100% of unit tests and the ~50 converted black-box conformance tests pass on Windows.
```

---

## Prompt 2: Phase 3 — Document Migration (`hoshin migrate`)

`````
We are executing **Rollout Phase 3 (Document migration)** in this `Hoshin.Net` repository, following `Hoshin.Net: a .NET workflow driven by harness hooks.md`.

### Context
- Phase 2 is complete: `Hoshin.Workflow` readers and `Hoshin.Cli` commands pass the JSONC black-box conformance suite.
- Reference the legacy YAML document formats in `D:/workspaces/hoshin/tools/documents/` and `D:/Repos/experiments/hoshin-net/` (the spike's YAML-to-JSONC converter).

### Phase 3 Tasks
1. **Implement `hoshin migrate` (`Hoshin.Workflow` + `Hoshin.Cli`):**
   - Rewrite an existing repository's YAML Markdown documents (`intent.md`, `spec.md`, `plan.md`, `build-log.md`, `review.md`) and `change.yml` into Layout 2 JSONC and `change.jsonc`, line for line.
   - Convert both **Layout 1** and **Layout 2** YAML documents into canonical **Layout 2 JSONC** (fenced ```` ```jsonc ```` front matter on line 1 and fenced ```` ```jsonc ```` record blocks).
   - Preserve comments and field ordering wherever possible.
   - Make `hoshin migrate` idempotent (already-migrated JSONC files are left untouched) and support `--dry-run`.
2. **Add Migration Tests (`tests/Hoshin.Tests`):**
   - Test Layout 1 -> Layout 2 JSONC conversion, Layout 2 YAML -> JSONC conversion, `change.yml` -> `change.jsonc`, comment preservation, and idempotency.

### Phase 3 Exit Gate
- Copy a real intent repository into a temporary test directory, run `hoshin migrate`, and verify that `hoshin check` and `hoshin read --json` succeed with zero errors and produce identical semantic output to the pre-migration TypeScript baseline.
`````

---

## Prompt 3: Phase 4 — `State`, `Next(State)`, Dual-Harness Hooks, and `build` / `accept`

```
We are executing **Rollout Phase 4 (`Next`, dual-harness hooks, and the build skill)** in this `Hoshin.Net` repository, following `Hoshin.Net: a .NET workflow driven by harness hooks.md`.

### Context & References
1. `Hoshin.Net: a .NET workflow driven by harness hooks.md` (sections: *The shared library*, *Harness plugins: Claude Code and Antigravity*, *Run state*, and *Distribution and compatibility*).
2. `D:/Repos/experiments/hoshin-net/findings.md` (specifically the Spike 2–5 findings and verified Antigravity/Claude Code hook payload & response contracts).
3. `D:/workspaces/hoshin/skills/build/` and `D:/workspaces/hoshin/skills/accept/` (extract judgment instructions only; discard all CLI command sequencing).

### Phase 4 Tasks
1. **State, Ledger, and `Workflow.Next(State)` (`Hoshin.Workflow`):**
   - Implement `State` computation from git, JSONC change documents, and the local ledger in `.git/`.
   - Implement the pure `Workflow.Next(State)` state machine covering the entire change lifecycle (`WriteIntent` -> `AwaitAccept(intent)` -> `WriteSpec` -> `AwaitAccept(spec)` -> `WritePlan` -> `AwaitAccept(plan)` -> `Implement` / `Retry` -> `RunChecks` -> `NeedsApproval` -> `Publish` -> `Done`), keeping all step ordering, retry caps, and gate logic in C#.
   - Write exhaustive unit tests for `Workflow.Next(State)`: given state X, assert exact step Y across every transition, retry limit, and out-of-order gate refusal.

2. **Library-Owned Execution Steps (`Hoshin.Workflow`):**
   - Implement the allow-listed command runner (`Red` on a cached detached worktree, `Baseline`, `RunChecks` for `build.finish` hooks, and `Git` two-phase commits, push, and PR creation).
   - Enforce the strongly-typed Red/Green commit split: commit test files first (`test(<scope>): <task name>` with `Plan-Task` and `Generated-By` trailers), observe Red (failing assertion OR missing-symbol compilation error `CS0246`/`CS0103`/`CS1061`/`TS2304` that resolves in Green), then commit implementation files (`feat`/`fix`) and observe Green at `HEAD`. Failed attempts commit nothing and pass failure diagnostics as `Retry` handoff.

3. **Dual-Harness Hook Adapter & Guard (`Hoshin.Cli`):**
   - Implement `hoshin hook <start|result|subagent-stop|stop>` and `hoshin guard` using the Spike 5 `HarnessAdapter` to normalize both **Google Antigravity** and **Claude Code** `stdin` payloads.
   - Support both foreground and async background subagents (`agentId` / `taskId` tracked in the ledger; judge only upon completion).
   - Implement the session-aware fast-path step file (`session_id` / `conversationId` + timestamp) beside the ledger so `guard` never recomputes `State` and never locks out unrelated sessions.
   - Ensure every hook exits `0` and writes errors to `stderr`.

4. **Harness-Neutral Skills & Dual-Bundle Packaging (`skills/` + `packaging/`):**
   - Author `skills/build/procedure.md` and `skills/accept/procedure.md` with **zero** CLI commands, workflow rules, or harness tool names (`Agent`, `invoke_subagent`, `Bash`, `run_command`).
   - Add a release packaging script/target that builds the portable `hoshin` DLL bundle (`hoshin.dll`, `Hoshin.Workflow.dll`, `Markdig.dll`, `Gherkin.dll`) and generates both the **Claude Code** plugin layout (`.claude-plugin/`) and the **Antigravity** bundle layout (`.agents/`).

### Phase 4 Exit Gate
- All `Workflow.Next(State)` unit tests pass.
- End-to-end scripted hook integration tests pass for both **Claude Code** and **Antigravity** JSON payloads (covering foreground subagents, async subagents, `guard` denials on premature prod edits and manual `git commit`, retry handoffs, `RunChecks`, and `accept` -> `Publish`).
```

---

## Prompt 4: Phase 5 — Remaining Skills (`intent`, `spec`, `plan`, `review`, `fix`, `done`, `setup`, `context`)

`````
We are executing **Rollout Phase 5 (Intent, spec, plan, fix, review and done)** in this `Hoshin.Net` repository, following `Hoshin.Net: a .NET workflow driven by harness hooks.md`.

### Context & References
1. `Hoshin.Net: a .NET workflow driven by harness hooks.md` (specifically *Intent, spec and plan*).
2. `D:/workspaces/hoshin/skills/` for `intent`, `spec`, `plan`, `review`, `fix`, `done`, `setup`, and `context`.

### Phase 5 Tasks
1. **Authoring & Review Gates (`Hoshin.Workflow` + `Hoshin.Cli`):**
   - Wire `hook start`, `guard`, and `hook stop` for `WriteIntent`, `WriteSpec`, `WritePlan`, `Review`, and `Fix`:
     - `Stop` stays silent when the target document is unchanged (open interview question), blocks with diagnostics when changed with validation errors (including Cucumber `Gherkin` spec rule checks `@R<n>` / `@from:`), and auto-wraps bare line-1 JSONC front matter in ```` ```jsonc ```` fences before committing clean drafts with source pins (`source_intent_commit`, etc.).
     - `guard` restricts file mutations strictly to the active step's target document and blocks manual git commits.
2. **Rewrite Remaining Skill Procedures (`skills/*/procedure.md`):**
   - Port the judgment-only instructions for `intent`, `spec`, `plan`, `review`, `fix`, `done`, `setup`, and `context` into `skills/<name>/procedure.md`, stripping all CLI commands, state checks, and harness-specific tool names.
   - Update the release packager to emit one-line `SKILL.md` wrappers for all 10 skills in both `.claude-plugin/` and `.agents/skills/`.

### Phase 5 Exit Gate
- Multi-turn scripted hook tests pass for `intent` (multi-question interview where `Stop` stays silent until a draft is written), `spec` (Gherkin rule failure block -> fix -> pin & commit), `plan`, `review`, and `fix` across both Claude Code and Antigravity payloads.
- Automated lint test confirms zero files under `skills/` contain YAML front matter, `hoshin` CLI invocations, or harness-specific tool names.
`````

---

## Prompt 5: Phase 6 — Release Cut & Retiring `tools/hoshin.ts`

```
We are executing **Rollout Phase 6 (Retire the TypeScript program)** from `Hoshin.Net: a .NET workflow driven by harness hooks.md`.

### Tasks
1. Package the vNext major release artifacts from `Hoshin.Net`:
   - Portable `hoshin` build (`hoshin.dll`, `Hoshin.Workflow.dll`, `Markdig.dll`, `Gherkin.dll`) + `skills/` folder.
   - `Hoshin.Workflow` NuGet package with embedded role texts.
   - Published Claude Code plugin bundle (`.claude-plugin/`) and Antigravity bundle (`.agents/`).
2. Generate a breaking-change migration note for the Kata Workbench (`tools/CLIENTS.md`) detailing the switch from `node tools/hoshin.ts` to `dotnet hoshin.dll` and YAML-to-JSONC front-matter updates.
3. Verify the full test suite and release packaging script run cleanly from a fresh clone on Windows.
```

