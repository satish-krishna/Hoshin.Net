# Hoshin.Net — Reviewer's Primer

This is a guided entry point for reading and reviewing the Hoshin.Net code. It covers:
- what the system is;
- how the repository is laid out;
- which files to read first, and in what order;
- the main runtime flows, traced through the code;
- where state lives;
- how to build and test.

The findings are in [ANALYSIS.md](ANALYSIS.md). This document only describes.

---

## 1. What Hoshin.Net is, in one minute

**hoshin** (the original, `satish-krishna/hoshin`) is a Claude Code plugin. It runs an engineering workflow:

> **intent → spec → plan → build → review → done**

Each step is a skill, a Markdown procedure that the model follows. A TypeScript CLI (`tools/hoshin.ts`) does the mechanical parts: validating documents, computing state, running lifecycle hooks and recording verdicts. The behavior is specified in `hoshin/tools/GRAMMAR.md`.

**Hoshin.Net** is the .NET 10 successor. It does three things:

1. **Ports the CLI** to C#: the same 22 commands, the same contract (`CLIENTS.md`), with JSONC documents instead of YAML.
2. **Moves orchestration out of the model** into a library. A pure state machine (`WorkflowEngine.Next`) decides the next step. A runner (`WorkflowRunner`) runs the tests, verifies Red then Green, and makes the commits itself. The model only writes documents, tests and code.
3. **Drives two harnesses**, Claude Code and Google Antigravity, through native hooks (`hoshin hook …`, `hoshin guard`).

> **Key fact for reviewers:** points 1 and 2 are currently **two separate systems** in the same codebase. See §4. Keep that in mind while reading, or the code will look more duplicated than it should.

---

## 2. Repository layout

```
Hoshin.Net/
├── Hoshin.sln, Directory.Build.props, global.json   # net10.0, nullable, version 2.0.0
├── README.md, DESIGN.md, AGENTS.md, CLIENTS.md, CHANGELOG.md
├── docs/KATA_MIGRATION.md                           # v1 → v2 guide for external callers
├── docs/review/                                     # ← this review (PRIMER.md, ANALYSIS.md)
├── skills/<name>/procedure.md                       # 11 harness-neutral role procedures (embedded into the DLL)
├── packaging/
│   ├── claude-code/{.claude-plugin/plugin.json, hooks/hooks.json}
│   ├── antigravity/hooks.json
│   └── package.ps1                                  # builds artifacts/ (Windows PowerShell)
├── src/
│   ├── Hoshin.Workflow/                             # the library (Markdig + Gherkin only)
│   └── Hoshin.Cli/                                  # the CLI → hoshin.dll
└── tests/Hoshin.Tests/
    ├── Unit/                                        # C# xUnit tests
    ├── Conformance/                                 # runs BlackBox/*.mjs and Examples/ as xUnit theories
    ├── BlackBox/*.test.mjs (+ helpers/)             # 44 node:test suites ported from hoshin/tools/test
    ├── Examples/<kind>/2/*.md + *.expected.json     # document reader golden files
    └── Fixtures/                                    # state-golden and part-2 sample change
```

### 2.1 `src/Hoshin.Workflow` — folder by folder

| Folder | Responsibility | Start with |
| :--- | :--- | :--- |
| `Engine/` | **System B core.** The pure state machine, step records, the ledger contract, the Red/Green runner, briefs for agents, the allow-listed process runner. | `WorkflowContracts.cs` → `WorkflowEngine.cs` → `WorkflowRunner.cs` |
| `Documents/` | The Markdown document model (`Doc`: lines, headings and fences via Markdig) and line-1 JSONC front matter (parse, check, edit). Readers for intent, spec (Gherkin), plan, build log and review. Embedded JSON schemas. | `Doc.cs`, `FrontMatter.cs`, `PlanReader.cs`, `SpecReader.cs` |
| `Common/` | JSONC helpers on `System.Text.Json`, a minimal draft-07 schema validator, the `Problem` record, exit codes, and `CouldNotRunException`. | `Jsonc.cs`, `Problems.cs` |
| `Changes/` | Per-change domain logic (System A). See below. | `ChangeState.cs`, `ChangeRecord.cs` |
| `Events/` | The lifecycle event runner (System A). See below. | `EventRunner.cs`, `Steps/IEventStep.cs` |
| `Git/` | `ICommandRunner` (processes), `IGitClient` (the git CLI wrapper used by System A), `IPullRequestClient` (the `gh` CLI), path canonicalization. | `GitClient.cs`, `CommandRunner.cs` |
| `Baseline/` | Runs `build.finish` checks at the base commit, to record failures that already exist there, plus baseline approvals. | `BaselineTaker.cs` |
| `Red/` | The legacy `hoshin red`: runs a task's test at its test-only commit and records the "observed red" verdict. | `RedRecorder.cs` |
| `Workspace/` | Everything about the workspace. See below. | `Marker.cs`, `WorkspaceResolver.cs` |
| `Migration/` | A hand-written YAML-subset → JSONC converter, and the document migrator (YAML layout 1/2 → JSONC layout 2; `change.yml` → `change.jsonc`). | `DocumentMigrator.cs` |

**`Changes/` in more detail:**
- the `change.jsonc` record;
- finding and placing changes, and computing their state (`ChangeState`, `FixState` for the light track, `Progress`, `Schedule`, `FinishedState`);
- cross-document checks (`CrossChecker`);
- `hoshin check` and `hoshin commit` (`DocumentCheckService`, `Committer`);
- rename and remove.

**`Events/` in more detail:** it runs `change.start`, `doc.accept`, `build.start`, `task.done`, `build.finish`, `review.finish` and `change.done`. For each event it runs the preconditions, the before and after hooks, and the built-in step. It writes the per-repo ledger and `checks.jsonl`. The folder also holds hook config parsing and hook script export.

**`Workspace/` in more detail:**
- reading and validating the `.hoshin.json` marker;
- resolving the workspace, master repo and systems;
- single-repo app folders;
- branch and naming rules;
- the managed `CLAUDE.md` block;
- stale checks;
- the legacy `ai-sdlc` → `hoshin` workspace migration.

### 2.2 `src/Hoshin.Cli`

| File or folder | Responsibility |
| :--- | :--- |
| `Program.cs` | Registers the 23 commands (`IHoshinCommand`). Dispatches on `args[0]`. Maps exceptions to exit codes. |
| `HoshinServices.cs` | **The composition root.** It builds about 45 services eagerly and wires System A and System B side by side. Read it to see what depends on what. |
| `OptionParser.cs`, `CliIo.cs` | `--name value` / `--flag` parsing; raw UTF-8 stdout and stderr (avoids Windows codepage issues). |
| `Commands/*.cs` | One class per CLI command. Most are thin: parse the options, call a service, print. Notable ones are `GuardCommand` (static and session-aware deny rules), `HookCommand` (dispatches to `HookOrchestrator`) and `MigrateCommand`. |
| `Hooks/HarnessAdapter.cs` | Detects the harness from the payload: snake_case means Claude Code, camelCase means Antigravity. Normalizes it into `NormalizedHookEvent`, and formats the harness-specific reply (`additionalContext`/`decision:block` vs `injectSteps`/`continue`). |
| `Hooks/HookOrchestrator.cs` | **System B's driver.** Handles `start`, `result`, `subagent-stop` and `stop`, and calls the engine and the runner. |
| `Hooks/HookInstructions.cs` | Turns a `Step` into the text injected into the model's context. |

---

## 3. Core concepts and vocabulary

| Term | Meaning |
| :--- | :--- |
| **Workspace / system / repo** | `.hoshin.json` (the marker) declares systems, each made of one or more repos. Change documents live in `intent/<system>/<change>/`. The single-repo and toy layout uses `.hoshin/`. |
| **Change** | One unit of work: `change.jsonc` (scope, base, branch, type) plus `intent.md`, `spec.md`, `plan.md`, `build/<repo>.md` and `build/review.md`. |
| **Layout 2 JSONC** | Each document starts on line 1 with a fenced ```` ```jsonc ```` front-matter block. Plan tasks, build-log entries and review findings are ```` ```jsonc ```` blocks under headings. YAML is accepted only by `hoshin migrate`. |
| **Status / pins** | Front matter `status: draft\|accepted\|rejected`. A spec pins `source_intent_commit`; a plan pins `source_spec_commit`. |
| **Task** | A plan entry with `id`, `test` argv, optional `compile` argv, and optional `files`. Its test command is the verdict. |
| **Red / Green** | Red: the new test fails *without* the production change (an assertion failure, or a missing-symbol compile error). Green: it passes *with* it. |
| **Verdict** | The library's judgment of one attempt: passed or failed, with reasons and the commits made. At most 3 attempts per task (`WorkflowEngine.MaxAttempts`). |
| **Step** | What `WorkflowEngine.Next` returns: `WriteDocument`, `AwaitAccept`, `Implement`, `Retry`, `RunChecks`, `NeedsApproval`, `Publish` or `Done`. `ReviewDocument` and `WriteFix` exist too, but are built by the orchestrator. |
| **Light track (`fix`)** | Small changes: intent, spec and plan are drafted together, with at most 3 tasks in one repo. |
| **Guard** | The `PreToolUse` hook that denies certain tool calls: pushes, manual commits, edits to protected files, production edits before any test exists. |
| **Harness** | The agent host: Claude Code or Antigravity. |

---

## 4. The two systems

| | **System A: the ported CLI** | **System B: the new hook path** |
| :--- | :--- | :--- |
| Entry points | `hoshin check / read / state / list / change / commit / run / red / baseline / …` | `hoshin hook start\|result\|subagent-stop\|stop`, `hoshin guard` |
| Who calls it | Kata Workbench, CI, scripts, the black-box suites | The harness hooks in `packaging/*/hooks.json` |
| Core types | `ChangeStateService`, `EventRunner` + `IEventStep`, `LedgerStore`, `RedRecorder`, `BaselineTaker` | `WorkflowEngine`, `WorkflowRunner`, `WorkflowRepo`, `HookOrchestrator` |
| Git access | `IGitClient` (`ProcessGitClient`) over `ICommandRunner` | `WorkflowRepo.Git` over `IAllowListedCommandRunner` |
| State on disk | `.git/hoshin/<system>/<change>/<repo>.json`, `checks.jsonl` | `.git/hoshin/workflow-ledger.json`, `step.json`, `run.json` |
| Behavior spec | GRAMMAR.md (in the hoshin repo) and CLIENTS.md | DESIGN.md §3–5 |
| Tests | `BlackBox/*.test.mjs` (44 suites), most of `Unit/` | `Unit/WorkflowNextTests.cs`, `Unit/DualHarnessHookIntegrationTests.cs`, `Unit/AuthoringAndReviewHookTests.cs` |

They share the document readers (`Documents/`) and `IPullRequestClient`. The diagram in ANALYSIS.md §3.2 shows where each concern is implemented twice.

---

## 5. Suggested reading order (about half a day)

1. **The intent.** Read `README.md`, then `DESIGN.md`. Skim `CLIENTS.md`. Optionally skim `hoshin/tools/GRAMMAR.md` for the original rules.
2. **The data model.** Read `src/Hoshin.Workflow/Engine/WorkflowContracts.cs`. It's about 180 lines of records: tasks, plans, steps, the ledger, `CurrentStep` and `RunMarker`. Everything in System B is built from these.
3. **The state machine.** `Engine/WorkflowEngine.cs`:
   - `WorkflowState.Read` builds the snapshot;
   - `Next` is the whole lifecycle in about 70 lines;
   - `Gate` decides whether a skill may run now.

   Read it alongside `tests/Hoshin.Tests/Unit/WorkflowNextTests.cs`.
4. **Where state comes from.** `Engine/WorkflowRepo.cs`: change-folder resolution, plan loading, document status (git show + git status), the ledger with its lock and atomic write, and test-path globbing.
5. **The Red/Green runner.** In `Engine/WorkflowRunner.cs`, read `EvaluateAndCommitTask` first. It is the heart of the "the library owns commits" idea. Then read `ObserveRedOnCachedWorktree`, then `RedClassifier.cs`.
6. **The hook layer.** `Cli/Hooks/HarnessAdapter.cs` (`Parse`, then the output builders), then `HookOrchestrator.HandleStart`, then `HandleStop`, then `Cli/Commands/GuardCommand.cs` (`Evaluate`, `StaticRefusal`). Compare with `packaging/claude-code/hooks/hooks.json`.
7. **The documents.** `Documents/Doc.cs` and `FrontMatter.cs`, then one reader end to end. `PlanReader.cs` is a good choice. Then look at its golden files in `tests/Hoshin.Tests/Examples/plan/2/`.
8. **System A**, as needed. Start from `Cli/HoshinServices.cs` and follow one command, for example `state` → `ChangeStateService`, or `run` → `EventRunner` → `Events/Steps/*`.
9. **The edges.** `Migration/` (large; read `DocumentMigrator.MigrateFile` and the converter's `ParseRoot`), `packaging/package.ps1`, and `skills/*/procedure.md`.

---

## 6. Runtime flows, traced

### 6.1 `/hoshin:build` in Claude Code, foreground subagent

```mermaid
sequenceDiagram
    participant U as Engineer
    participant CC as Claude Code
    participant H as hoshin hook / guard
    participant E as WorkflowEngine
    participant R as WorkflowRunner
    participant G as git + .git/hoshin

    U->>CC: /hoshin:build
    CC->>H: UserPromptExpansion → hook start
    H->>E: WorkflowState.Read → Gate("build") → Next
    E-->>H: Implement(T1)
    H->>G: write run.json, step.json (Issue)
    H-->>CC: additionalContext: "launch subagent with this prompt"
    CC->>CC: Agent tool (implement T1)
    loop each Edit/Write/Bash
        CC->>H: PreToolUse → guard (static rules, then step rules)
    end
    CC->>H: PostToolUse(Agent) → hook result
    H->>R: EvaluateAndCommitTask(T1)
    R->>R: Green on working tree → Red in .git/hoshin/worktrees/red-check
    R->>G: commit test(...) then feat(...); record verdict
    H->>E: Next → Implement(T2) or RunChecks
    H-->>CC: next brief
    CC->>H: Stop → hook stop
    H->>R: RunChecks (if next) → ledger
    H-->>CC: decision:block with next step, or allow at NeedsApproval
```

Code path:
- `HookCommand.RunAsync` → `HookOrchestrator.HandleStart`
- → `WorkflowEngine.Gate` / `Next`
- → `WorkflowRunner.Issue`
- → `HookInstructions.For`
- → `HarnessAdapter.ContextOutput`

### 6.2 Async (background) subagents

1. `hook result` sees `tool_response.status == "async_launched"` and records `ActiveSubagent` in the ledger.
2. `hook stop` yields while `background_tasks` is non-empty.
3. `hook subagent-stop` (Claude Code) judges the attempt. Antigravity has no such hook, so the attempt is judged on the parent's `Stop` with `fullyIdle: true`.

See DESIGN.md §5 and `DualHarnessHookIntegrationTests.ClaudeCode_AsyncBackgroundSubagent_*`.

### 6.3 Authoring a document (intent, spec or plan)

1. `hook start` → `WriteDocument` → a brief from `WorkflowBriefs.Intent|Spec|Plan`.
2. The model interviews the engineer and writes the file. During this step the guard only allows edits to that one document.
3. `hook stop` → `StopWhileWriting` → `WorkflowRunner.CommitDocument`. This auto-wraps bare JSONC front matter, validates the document, pins `source_*_commit`, and commits.
4. `/hoshin:accept` → `AcceptDocument` sets `status: accepted` and commits.

### 6.4 The CLI (System A), e.g. `hoshin state --json`

`Program` → `StateCommand` → `ChangeStateService` (locate the change, read its documents at HEAD, compute progress and the next skill) → JSON to stdout. The CLI contract and exit codes are in `CLIENTS.md`, and are enforced by `BlackBox/clients.test.mjs`.

---

## 7. Where state lives

| Path | Written by | Purpose |
| :--- | :--- | :--- |
| `intent/<system>/<change>/*.md`, `change.jsonc` | model + library commits | The change documents. Committed to git. |
| `.git/hoshin/workflow-ledger.json` (+ `.lock`) | `WorkflowRunner` | System B: tasks issued, verdicts, check runs, baselines, approvals, publishes, the active subagent. |
| `.git/hoshin/step.json`, `run.json` | `WorkflowRunner.Issue`, `HookOrchestrator` | The current step and the owning session, for the guard's fast path (24-hour staleness window). |
| `.git/hoshin/stop-blocks`, `last-start.txt`, `pending-context.txt` | `HookOrchestrator` | Turn bookkeeping: consecutive Stop blocks, start dedup, context queued for Antigravity. |
| `.git/hoshin/hooks.jsonl` | `HarnessAdapter.AppendTrace` | A trace of every hook and guard decision. |
| `.git/hoshin/worktrees/red-check/` | `WorkflowRunner` | The cached detached worktree used for the Red check. |
| `.git/hoshin/<system>/<change>/<repo>.json`, `checks.jsonl` | `LedgerStore`, `CheckLog` | System A: event runs, reds, baseline approvals, gate blocks, check events. |

`.git/hoshin` lives in the git *common* dir, so linked worktrees share it.

---

## 8. Building, testing and running locally

Prerequisites:
- the .NET 10 SDK (`global.json` pins 10.0.102 with `latestFeature` roll-forward);
- `git`;
- Node 18+ (for the black-box suites).

```bash
dotnet build Hoshin.sln                                   # 0 warnings expected

# fast: C# tests only (~1 min)
dotnet test tests/Hoshin.Tests --filter "FullyQualifiedName!~BlackBoxConformanceTests"

# full (~7 min on Linux; the 44 node suites run sequentially)
dotnet test tests/Hoshin.Tests

# run one black-box suite directly against the built CLI
TEST_HOSHIN_DLL=$PWD/src/Hoshin.Cli/bin/Debug/net10.0/hoshin.dll \
  node --test tests/Hoshin.Tests/BlackBox/state.test.mjs

# poke the CLI or a hook by hand
dotnet src/Hoshin.Cli/bin/Debug/net10.0/hoshin.dll state --json
echo '{"hook_event_name":"PreToolUse","session_id":"s","cwd":"'$PWD'","tool_name":"Bash","tool_input":{"command":"git push"}}' \
  | dotnet src/Hoshin.Cli/bin/Debug/net10.0/hoshin.dll guard
```

Gotchas:
- `Packager_EmitsOneLineSkillWrappersForAll10SkillsInBothBundles` calls Windows `powershell` and fails elsewhere. Expect 128/129 on Linux and macOS.
- The tests need a git identity (`user.name` / `user.email`) available to child processes.
- `AllowListedCommandRunner` strips most environment variables from test commands. If a task's test needs a proxy, an SSH agent or a toolchain variable, it won't see it.

---

## 9. Review checklist by area

Use these questions as you read each part. ANALYSIS.md has the answers found so far.

- **Engine:**
  - Is every decision `Next` makes derivable from the ledger and git alone?
  - Are ledger records scoped to a change?
  - What happens when two hooks run at once?
- **Runner:**
  - Which files end up in a commit?
  - What happens to git and the ledger at each failure point in `EvaluateAndCommitTask`?
  - Can a bad Red be accepted?
- **Guard:**
  - Can the agent reach the same effect through another tool, another spelling, or by deleting state?
  - Does the guard stay silent outside hoshin?
- **Orchestrator:**
  - Who can trigger approval?
  - Which session is a hook acting for?
  - Is every exit path reported to the engineer?
- **Documents:**
  - Does each reader agree with the migrator and with the schema about what is valid?
- **Composition:**
  - Is each dependency injected, or found through a static `Default`?
  - Does a command get only what it needs?
- **Parity:**
  - For each GRAMMAR.md rule you rely on, which system implements it?
