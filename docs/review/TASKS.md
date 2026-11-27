# Hoshin.Net — Review Tasks (agent-actionable)

This file turns [ANALYSIS.md](ANALYSIS.md) into discrete work items that a coding agent can pick up one at a time. Each task gives:
- the files and symbols involved;
- the problem, as a concrete failure;
- what to change;
- the test to write **first**;
- when the task is done.

The IDs in brackets (S1, E2, L4, …) point back to the matching section of ANALYSIS.md, which has the full reasoning.

**Round 1** (the tasks below, `T01`–`T43`, `A1`–`A8`) is done; it landed as PR #5. **Round 2** (`R00`–`R09`) is under "Round 2 — close the lifecycle"; it is PR #6. **Round 3** (`R10`–`R40` and `B01`–`B15`) is at the end of this file, under "Round 3 — ready to replace hoshin". It takes in round 2's later tiers and EPIC-B. Each round has its own rules: read them before starting a task in that round.

---

## How to work through this file (instructions for the agent)

0. **[CONTRIBUTING.md](../../CONTRIBUTING.md) is mandatory for every task.** That covers Conventional Commits, test first, the SOLID design rules (§3), the code rules (§4), coverage, and document ownership. Every pull request fills in `.github/pull_request_template.md`, including the design checklist. "Done when" in each task below always also means:
   - the change conforms to CONTRIBUTING.md;
   - it adds no new architecture-baseline violation;
   - it removes the baseline entries for any violation it fixed.

   When a task adds behavior to a class that is already too large or has baselined violations (for example `WorkflowRunner`, `WorkflowRepo`, `HookOrchestrator`, `GuardCommand`), put the new logic in a new, focused, injected type rather than growing the class.
1. **Take one task at a time**, in the order of the index below, unless the task says otherwise. **Start with the "Gate" tasks (G)**: they make the rules enforceable, so CI checks every later task. Delivery is described in "Delivery: one pull request, one commit per task" below.
2. **Never start a task marked `needs decision` until the decision below has an answer recorded under "Answer".** If it has none, skip the task and say so.
3. **Test first.** Write the test in "Test first", run it, and confirm it fails for the reason described. Then change the code and confirm it passes. Don't weaken, skip or delete an existing test to get green. If an existing test asserts the old, wrong behavior, change it only when the task says so, and say why in the PR.
4. **Every commit is green on its own,** so the history can be bisected:
   ```bash
   dotnet build Hoshin.sln            # 0 warnings, at every commit
   dotnet test tests/Hoshin.Tests --filter "FullyQualifiedName!~BlackBoxConformanceTests"   # at every commit
   dotnet test tests/Hoshin.Tests     # full (~7 min): at the last commit of each section (G, P0, P1, P2) and before marking the PR ready
   ```
   Until T32 lands, `Packager_*` needs Windows PowerShell. That's why T32 is the first commit.
5. **Keep the AGENTS.md invariants:**
   - `Hoshin.Workflow` references only Markdig and Gherkin.
   - Harness-specific code stays in `src/Hoshin.Cli/Hooks` and `src/Hoshin.Cli/Commands`.
   - `skills/*/procedure.md` files contain no CLI commands, YAML or harness tool names.
6. **When behavior changes, update the docs in the same commit as the task:**
   - `DESIGN.md` for architecture or hook behavior;
   - `CLIENTS.md` for CLI commands, options, fields or messages (enforced by `BlackBox/clients.test.mjs`);
   - `CHANGELOG.md` under an `Unreleased` heading.
7. **Update the Status column** in the index *in the task's own commit*: `done` when you finish it, or `invalid: <reason>` when you find the task is wrong (that is a commit of its own, `docs(review): mark Tnn invalid`). Don't silently expand a task's scope. If you find a new problem, add it at the end of the index as `T-new-<n>`.
8. **Stay within scope.** Symbols are named rather than line numbers wherever possible, so the task stays valid after edits. If a symbol isn't where the task says, search for it before concluding the task is stale.

### Delivery: one pull request, one commit per task

The whole program lands as **one pull request**, with **exactly one commit per task**. The engineer chose this.

- **Branch:** `fix/review-findings`, from `main`, once the pull request that adds this file and CONTRIBUTING.md has merged.
- **Pull request:** open it as a **draft** right after the first task's commit, and push after every task so CI runs on each step.
  - **Title:** `fix(workflow)!: address review findings`. It is breaking: A2, T31 and others change client-visible behavior.
  - **Description:** follows `.github/pull_request_template.md`. Its "Tasks" table lists every task with its commit subject and status, and is kept current on every push.
  - **Ready for review:** mark it ready only when every task is `done` or `invalid`, apart from EPIC-B, which stays out of this PR (see below).
- **Commits:** one per task, in index order. Don't squash several tasks together, and don't split a task across commits. If review asks for changes to a task, amend that task's commit so the rule still holds: `git commit --fixup <sha>`, then `GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/main` (non-interactive), then `git push --force-with-lease`. That is the only history rewrite allowed, and only on this branch. Each commit's message has:
  - a Conventional header in the CONTRIBUTING.md §1 format;
  - a body saying what changed and why;
  - these trailers:
    ```
    Task: T01
    Test-first: <test name> failed before the change: <one-line failure>
    Docs: <owner documents updated, or "none">
    ```
  For example: `fix(guard): stay silent outside an active hoshin run`, with `Task: T01`.
- **Merge:** by squash, as usual. The per-task commits exist for review: a reviewer can step through the branch one task at a time. So keep them clean until the merge, and never squash them on the branch.
- **EPIC-B** (skill guidance, templates and evals) is judgment-heavy writing that needs a person, so it is **not** part of this pull request.

---

## Decisions needed from the engineer

These change behavior or architecture, so they belong to a person, not the agent. Each has a recommendation; the tasks that depend on it say so.

### D1 — Which workflow model is canonical?
Hoshin.Net contains two systems (ANALYSIS §3.2):
- **System A**, the ported CLI: `ChangeState`, `EventRunner`, `LedgerStore`, `IGitClient`, base-branch hooks.
- **System B**, the new hook path: `WorkflowEngine`, `WorkflowRunner`, `WorkflowRepo`, `WorkflowLedger`.

The options:
- **(a, recommended)** Keep System B's engine and harness adapter, but drive them from System A's domain services, with one git layer and one ledger.
- **(b)** Make System B self-sufficient and retire System A, apart from the CLI contract.
- **(c)** Keep both, and document System B as single-repo only.

**Answer: (a).** Decided by the engineer, 2026-11-22. The section "P1 — Unify the two systems" breaks this into tasks A1–A8. To change this answer, edit it here and re-plan A1–A8 before any of them starts.

### D2 — When does the guard apply?
Today the static rules (no push, no PR, protected files) apply in **every repository on the machine** (S2). The options:
- **(a, recommended; matches the original GRAMMAR)** Only while a hoshin run is active for the calling session.
- **(b)** Anywhere inside a hoshin workspace (a `.hoshin.json` found upward from `cwd`, or a `.hoshin/` folder), for every session.

Either way, the guard must stay silent outside hoshin workspaces.

**Answer: (a).** Decided by the engineer, 2026-11-22: the guard applies only while a hoshin run is active for the calling session (the session in `run.json`, or its registered subagent). Every other session, in any repository, gets no guard output.

### D3 — How should `hoshin migrate` parse YAML?
The hand-written converter loses data (L1–L3). The options:
- **(a, recommended)** Move `migrate` into a new `Hoshin.Migration` assembly referenced only by `Hoshin.Cli`, and use YamlDotNet there. `Hoshin.Workflow` stays dependency-free.
- **(b)** Keep the hand-written converter, but make it refuse every construct it doesn't fully support.

**Answer: (a).** Decided by the engineer, 2026-11-22: a new `Hoshin.Migration` assembly uses YamlDotNet, and `Hoshin.Workflow` keeps only Markdig and Gherkin. T10 (fail closed) still lands first, as a safety net.

### D4 — Publishing defaults
- **Branch:** `hoshin/<change>` (today) or `change/<change>` (the original GRAMMAR).
- **PR:** open one on publish, or not (today: never).
- **Review gate:** require a review with no high findings before publishing (the original), or not (today: none).

Recommended: `change/<change>`, open a PR, and require the review gate.

**Answer: as recommended.** Decided by the engineer, 2026-11-22:
- the default branch is `change/<change>` (`branch` in `change.jsonc` still overrides it);
- publishing opens a PR against `base`, or reuses an open one;
- publishing requires a committed review at the current head with no `high` findings.

### D5 — Tasks without testable behavior
GRAMMAR allows plan tasks that have no test (config, docs) and tasks that only add a protecting test. System B refuses both: `WorkflowRepo.ReadPlan` throws, and `EvaluateAndCommitTask` demands both test and production changes. Allow them, with a different verdict path, or keep them refused and document it?

Recommended: allow them, as the original does.

**Answer: allow them.** Decided by the engineer, 2026-11-22, following the original's rule (`hoshin/skills/plan/references/drafting.md`): "A task with no testable behavior (configuration, documentation) says so, has no `test` and no `compile`, and its 'done when' is the check." See T41.

---

## Index

The order is the order of work. **G** tasks make CONTRIBUTING.md enforceable, so they go first. **SOLID** marks tasks that fix existing design-rule violations. Those are required, not optional, and they are scheduled after the P0 fixes because the P0 fixes must not wait on refactoring.

| ID | Priority | Title | Fixes | Needs | Status |
| :--- | :--- | :--- | :--- | :--- | :--- |
| T32 | G | Cross-platform packager and CI (Linux + Windows) | §8.6 | — | done |
| T33 | G | Warnings as errors, analyzers, complexity limit, `.editorconfig` | §8.6 | — | done |
| T39 | G | Architecture tests with a shrinking baseline | CONTRIBUTING §3–§5 | — | done |
| T40 | G | Conventional names check | CONTRIBUTING §1 | — | done |
| T42 | G | Coverage gate on changed lines | CONTRIBUTING §6 T3 | T32 | done |
| T01 | P0 | Scope the guard and hook logging to the active run | S2, H8 | D2 ✔ | done |
| T02 | P0 | Make approval and acceptance human-only | S1 | — | done |
| T03 | P0 | Protect harness config and hoshin state from the agent | S4, S5 | — | done |
| T04 | P0 | Close the trivial guard bypasses, and enforce "no agent commits" in the library | S3 | — | done |
| T05 | P0 | Read build checks from the base branch, not the working tree | S6 | — | done |
| T06 | P0 | Protect the change folder and shell writes during a build | S7 | — | done |
| T07 | P0 | Scope the workflow ledger to one change | E1 | — | done |
| T08 | P0 | Commit only the task's own paths | E2, E11 | — | done |
| T09 | P0 | Serialize evaluate-and-commit | E3 | — | done |
| T10 | P0 | Make `migrate` fail closed | L1, L2, L3 | — | done |
| T12 | P0 | `Jsonc.TryParse` accepts empty and comment-only text | L4 | — | done |
| T13 | P0 | Rewrite `FrontMatterEdit.Set` on real JSONC tokens | L5 | — | done |
| T14 | P1 | Harden `RedClassifier` | E4 | — | done |
| T15 | P1 | Fix test-path classification and give it unit tests | E5 | — | done |
| T16 | P1 | Resolve relative executables against the working directory | E6 | — | done |
| T17 | P1 | Keep the environment that push, proxies and toolchains need | E7 | — | done |
| T18 | P1 | Make `ProcessCommandRunner` timeouts hold | L6 | — | done |
| T19 | P1 | Stop `LedgerStore` wiping a corrupt ledger; add a lock | L7 | — | done |
| T20 | P1 | `SpecReader` uses `Doc.Fences`; handle "Scenario Template" | L8, L12 | — | done |
| T22 | P1 | Claude Code `Stop` and `Result` act only for the run's session | S8 | — | done |
| T23 | P1 | `setup`/`context` don't take over the run | H2 | — | done |
| T24 | P1 | Judge only the dispatched implement subagent | H3 | — | done |
| T25 | P1 | Report verdicts and `NeedsApproval` in the Claude async flow | H5 | — | done |
| T26 | P1 | Separate stop counters; honor `stop_hook_active` | H6 | — | done |
| T27 | P1 | Truncate injected text without losing the closing marker | H7 | — | done |
| T28 | P1 | Make timeouts and the attempt cap configurable | E10 | — | done |
| T29 | P1 | Accept `testPatterns` in `.hoshin.json` | §7 | — | done |
| T30 | P1 | The intent brief finds the system's context files | §6 | — | done |
| T31 | P1 | Publishing: PR, review gate, branch default, diagnostics | E8 | D4 ✔ | done |
| T41 | P1 | Tasks with no testable behavior | §6 | D5 ✔ | done |
| T11 | P1 | Replace the YAML converter (`Hoshin.Migration` + YamlDotNet) | L1–L3 | D3 ✔, T10 | done |
| T34 | P1 · SOLID | Remove `?? Default` fallbacks; inject clock and file system | §8.5 (D1–D3) | T39 | done |
| T35 | P1 · SOLID | Give commands narrow dependencies; split fat interfaces | §8.4 (I1, I2) | T34 | done |
| T36 | P1 · SOLID | One owner for duplicated rules; enums for closed sets | E13 (O1, R1) | — | done |
| T43 | P1 · SOLID | Split `WorkflowRunner`, `WorkflowRepo` and `HookOrchestrator` | §8.1 (S1, S3) | T34 | done |
| A1 | P1 | Unify: `WorkflowRunner` uses `IGitClient` | §3.2 | D1 ✔, T35 | done |
| A2 | P1 | Unify: one Red rule | §3.2 | A1, T14 | done |
| A3 | P1 | Unify: one ledger (`LedgerStore`, per change and repo) | §3.2 | A1, T07, T19 | done |
| A4 | P1 | Unify: `WorkflowState` from `ChangeStateService` | §3.2, §6 | A1 | done |
| A5 | P1 | Unify: the runner fires lifecycle events and writes build logs | §6 | A3, A4 | done |
| A6 | P1 | Unify: multi-repo builds | §6 | A5 | done |
| A7 | P1 | Unify: start a change from `hook start` | H1 | A4 | done |
| A8 | P1 | Unify: reject, reopen and light-track accept flows; delete the unused half | §6 | A5, A7 | done |
| T21 | P2 | Small reader and validator fixes (batch) | L11, L12 | — | done |
| T37 | P2 | Fix false and stale doc claims | §7 | — | done |
| T38 | P2 | Recorded harness payload fixtures | §5.2, §9 | — | done |
| EPIC-B | P1 | Restore the dropped skill guidance, templates and evals | §6 | — | moved to round 3 (B01–B15) |

---

## G — Make the rules enforceable (do these first)

### T32 · Cross-platform packager and CI (Linux + Windows)

- **Do:**
  1. Port `packaging/package.ps1` to run under PowerShell 7 (`pwsh`) on Linux, macOS and Windows: `Join-Path` everywhere, no `\` literals. Alternatively, rewrite it as a small .NET console tool under `tools/`.
  2. Make `plugin.json`'s version come from `-Version` / `VersionPrefix`.
  3. Change `Packager_*` to invoke `pwsh`.
  4. Add `.github/workflows/ci.yml` with a matrix of `ubuntu-latest` and `windows-latest`. Steps: setup-dotnet 10, setup-node 22, `dotnet build -warnaserror`, `dotnet test`. Upload TRX results.
- **Note:** until this lands, `Packager_*` fails on Linux. That is expected, and it is not a reason to filter it out. Land this task first.
- **Done when:** CI is green on both operating systems, with the full suite including `Packager_*` and no test filter.

### T33 · Warnings as errors, analyzers, complexity limit, `.editorconfig`

- **Do:**
  1. In `Directory.Build.props`, add `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild=true`.
  2. Add an `.editorconfig` matching the current style (4 spaces, file-scoped namespaces, `var`).
  3. Enable CA1502 (cyclomatic complexity) with a `CodeMetricsConfig.txt` threshold of 15 (CONTRIBUTING.md S3).
  4. Fix each new warning, or suppress it with `[SuppressMessage(..., Justification = "baselined: fix in T43")]` on the member. Each suppression is a baseline entry: list it in `tests/Hoshin.Tests/Architecture/baseline.json` too, so T39's test keeps the count from growing.
  5. Replace per-call `new Regex(...)` in hot paths (`Doc.Split`, `FrontMatterEdit`, `KeyLine`, `FieldLine`) with `[GeneratedRegex]`.
- **Done when:** `dotnet build` is clean with the new settings and the tests pass.

### T39 · Architecture tests with a shrinking baseline

- **Fixes:** makes CONTRIBUTING.md §3–§5 enforceable.
- **Where:** new `tests/Hoshin.Tests/Architecture/` (`ArchitectureTests.cs`, `baseline.json`).
- **Do:** add tests that inspect the built assemblies (reflection) and `src/**/*.cs` (text scan, comments and strings stripped). Each rule reports violations as stable keys (for example `D2:Hoshin.Workflow.Engine.WorkflowRunner..ctor(commandRunner)`). The rules:

  | Rule | Check |
  | :--- | :--- |
  | **A1** | `Hoshin.Workflow` references only `Markdig`, `Gherkin`, their transitive packages, and `System.*`. It never references `Hoshin.Cli`. |
  | **A2** | No type outside `Hoshin.Cli.Hooks` and `Hoshin.Cli.Commands` mentions Claude Code or Antigravity payload names (`hook_event_name`, `hookEventName`, `toolCall`, `invoke_subagent`, …). |
  | **S2** | `Engine/WorkflowEngine.cs` and `Engine/RedClassifier.cs` don't use `System.IO`, `System.Diagnostics.Process`, `Environment`, or `DateTime(Offset).Now/UtcNow`. |
  | **S3** | No `src/**/*.cs` file over 400 lines. |
  | **I1** | No interface in `src/` with more than 10 members. |
  | **I2** | No constructor in `Hoshin.Cli.Commands` takes `HoshinServices`. |
  | **D1** | No `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now` or `DateTimeOffset.UtcNow` outside the clock adapter. |
  | **D2** | No public or internal constructor parameter whose type is an interface or a `Hoshin.*` class has a default value. |
  | **D3** | No `?? new <Type>(` or `?? <Type>.Default` in `src/` outside `HoshinServices.cs`. Every `static Default` property is listed in an allow-list with a reason. |
  | **D4** | No static field that is neither `readonly` nor `const`. |
  | **C3** | No `catch {` or `catch (Exception…)` block whose body neither throws nor calls a logger or trace. |

  Then:
  1. Generate `baseline.json` from today's code.
  2. The test fails if a violation is found that isn't in the baseline (the message names the rule and links to CONTRIBUTING.md).
  3. The test also fails if a baseline entry no longer violates (the message says "remove it from the baseline").
- **Test first:** for each rule, a fixture type or source snippet under `tests/Hoshin.Tests/Architecture/Samples/` (excluded from the scan of `src/`) that violates it, and a test asserting the rule reports it.
- **Done when:**
  - all rules run in CI;
  - the baseline matches today's code exactly;
  - CONTRIBUTING.md's "(lands with T39)" notes are removed;
  - the baseline count per rule is printed in the test output, so its shrinking is visible.

### T40 · Conventional names check

- **Fixes:** CONTRIBUTING.md §1
- **Do:**
  1. Add `.github/workflows/conventional.yml`, ported from `satish-krishna/hoshin` (`.github/workflows/conventional.yml`).
  2. Use the types from CONTRIBUTING.md §1, and pass the title and branch through `env`, never interpolated into the script.
  3. Check the PR title against `^(feat|fix|docs|test|refactor|perf|build|ci|chore)\([a-z0-9]+(-[a-z0-9]+)*\)!?: \S.*$`, and the branch against `^(feat|fix|docs|test|refactor|perf|build|ci|chore)/[a-z0-9]+(-[a-z0-9]+)*$`.
  4. Also check the header of **every commit** in the pull request (`git log --format=%s origin/<base>..HEAD`) against the title pattern. Reviewers step through program pull requests commit by commit, so each header must say what its task did.
  5. For a pull request whose branch is `fix/review-findings`, also require a `Task: <id>` trailer on every commit, naming an ID from `docs/review/TASKS.md`, with no ID used twice.
- **Done when:** the check runs on pull requests and is named `conventional names`, so it can be made required. Remove CONTRIBUTING.md's "(lands with T40)" note.

### T42 · Coverage gate on changed lines

- **Fixes:** CONTRIBUTING.md §6 T3
- **Depends on:** T32
- **Do:**
  1. In CI (the Linux job), collect coverage with `dotnet-coverage collect -f cobertura -o coverage.cobertura.xml "dotnet test tests/Hoshin.Tests"`.
     - `dotnet-coverage` also instruments the child `dotnet hoshin.dll` processes started by the black-box suites. Verify that: coverlet alone does not, and the CLI code would then look uncovered.
  2. Gate with `diff-cover coverage.cobertura.xml --compare-branch=origin/main --fail-under=90 --include 'src/**'`.
  3. Publish the HTML report as an artifact.
- **Test first:** in a throwaway branch, add an uncovered line to `src/` and confirm the job fails. Add a line covered only by a black-box suite and confirm it counts as covered. Describe both checks in the PR.
- **Done when:** the gate runs on every pull request that changes `src/`, and CONTRIBUTING.md's "(lands with T42)" note is removed.

---

## P0 — Safety and data integrity

### T01 · Scope the guard and hook logging to the active run  `security`

- **Fixes:** S2, H8
- **Where:**
  - `src/Hoshin.Cli/Commands/GuardCommand.cs`: `Evaluate`, `StaticRefusal`, and `RunAsync`, where `AppendTrace` is called;
  - `src/Hoshin.Cli/Hooks/HarnessAdapter.cs`: `AppendTrace`;
  - `src/Hoshin.Cli/Commands/HookCommand.cs`;
  - `tests/Hoshin.Tests/BlackBox/gate.test.mjs`.
- **Problem:** `Evaluate` calls `StaticRefusal` before it looks at the repository. With the plugin installed, `git push origin main` and `gh pr create` are denied in a plain `git init` repo with no hoshin files. Every guard call or hook also creates `.git/hoshin/hooks.jsonl` in that repo.
- **Do:**
  1. Work out the scope first: the payload's `cwd`, or the process cwd when `cwd` is missing.
  2. Per D2 (a), apply `StaticRefusal` and the step rules only when:
     - `run.json` exists in that repository's `<git-common-dir>/hoshin/`;
     - it is within the 24-hour window;
     - `IsSessionInActiveRun` holds for the event.

     Because of T22, "in the run" means the run's session or its registered subagent.
  3. Otherwise, return "allow" with no output. Don't create `.git/hoshin/`, and don't write a trace. Find the repository without creating anything.
  4. Log static denials too. Today they return `repo = null` and are never traced.
- **Test first:**
  - New black-box test in `gate.test.mjs`. In a temp `git init` repo with no `.hoshin.json`, `.hoshin/` or `intent/`:
    - `guard` with `{hook_event_name: "PreToolUse", cwd: <repo>, tool_name: "Bash", tool_input: {command: "git push origin main"}}` prints nothing;
    - `<repo>/.git/hoshin` doesn't exist afterwards.
  - Same for `gh pr create`, and for an `Edit` of `<repo>/.hoshin.json`.
  - In a hoshin workspace *with* an active run for session `s1`, `git push` from session `s2` prints nothing, and from `s1` is denied.
- **Existing tests to adapt:** `gate.test.mjs` sends guard payloads with **no `cwd`**, and expects static denials. Give those payloads a `session_id` and a `cwd` inside a workspace fixture with an active run for that session: write `run.json` and `step.json` the way `WorkflowRunner.Issue` does, through a small helper in `BlackBox/helpers/`. The assertions themselves stay the same.
- **Done when:** both new tests pass, all adapted `gate.test.mjs` cases still deny, and DESIGN.md §5 ("Session-Aware Guard Isolation") describes the scope.

### T02 · Make approval and acceptance human-only  `security`

- **Fixes:** S1
- **Where:**
  - `packaging/package.ps1`, the wrapper string written to each `SKILL.md`;
  - `src/Hoshin.Cli/Hooks/HookOrchestrator.cs`: `HandleStart` (the `invoked is "accept" or "done"` branch) and `DetectInvokedSkill`.
- **Problem:** at `NeedsApproval`, the model can call `Skill("hoshin:accept")` (Claude Code), or `view_file` on `.agents/skills/accept/SKILL.md` (Antigravity). `PostToolUse` runs `hook start`, which calls `IWorkflowRunner.Answer(approved: true, "approved by the engineer…")` and then publishes. `AwaitAccept` documents are accepted the same way.
- **Do:**
  1. Emit `disable-model-invocation: true` in the `SKILL.md` front matter for `accept` and `done`.
  2. Have `DetectInvokedSkill` report the *source* of the invocation:
     - `user`: Claude `UserPromptExpansion`, or Antigravity `PreInvocation` with a typed slash command;
     - `model`: `PostToolUse(Skill)` or a SKILL.md read.
  3. In `HandleStart`, only a `user`-sourced `accept`/`done` may call `Answer` or `AcceptDocument`.
  4. A model-sourced one must make no state change. It should reply with context telling the model to ask the engineer to type `/hoshin:accept`.
- **Test first:** in `DualHarnessHookIntegrationTests`, drive a change to `NeedsApproval`, then send a Claude `PostToolUse` with `tool_name: "Skill"` and `tool_input` naming `hoshin:accept`. Assert:
  - the ledger has no approval;
  - no publish happened (the remote has no new ref);
  - `Next` is still `NeedsApproval`.

  Add the same case for Antigravity (`view_file` of `.agents/skills/accept/SKILL.md`), and for `AwaitAccept` on the intent.
- **Done when:** these tests pass, the user-typed paths still approve (the existing E2E tests stay green), and `Packager_*` asserts the new front-matter line.

### T03 · Protect harness config and hoshin state from the agent  `security`

- **Fixes:** S4, S5
- **Where:** `src/Hoshin.Cli/Commands/GuardCommand.cs`: the `Protected` and `ProtectedRunDirs` patterns, and the checks that use them.
- **Problem:**
  - During a build, `echo '{"disableAllHooks":true}' > .claude/settings.local.json` is allowed.
  - `cd .git && cd hoshin && rm run.json step.json` and `rm -r "$(git rev-parse --git-common-dir)/hoshin"` are allowed.
  - Without `run.json`, every step rule switches off.
- **Do:**
  1. Add to the protected set:
     - `.claude/settings.json`, `.claude/settings.local.json` and `.claude/hooks/` (any path segment, case-insensitive on Windows and macOS);
     - `.git/hooks/` at all times, not only during a run;
     - the absolute path of `<git-common-dir>/hoshin`.
  2. Also deny shell commands that combine a write verb (the existing `Writes` regex) with any of the tokens `.git`, `hoshin`, `run.json`, `step.json`, `workflow-ledger`, or `git rev-parse --git-common-dir`. Prefer denying too much here; the denial message explains why.
  3. As defense in depth, make the orchestrator treat a missing `run.json` with a non-empty ledger and an unfinished `Next` as tampering. It should block Stop with a message, not report "no run".
- **Test first:** guard unit tests, with an active run fixture for the session, asserting `deny` for each of:
  - `echo x > .claude/settings.local.json`
  - `Write .claude/settings.json`
  - `cd .git && cd hoshin && rm run.json`
  - `rm -r "$(git rev-parse --git-common-dir)/hoshin"`
  - `Edit .git/hooks/pre-commit`

  Also an orchestrator test: delete `run.json` mid-build, then send Stop, and assert it blocks.
- **Done when:** these tests pass and the allow-list cases in `gate.test.mjs` (for example `cat .hoshin.json`) still allow.

### T04 · Close the trivial guard bypasses, and enforce "no agent commits" in the library  `security`

- **Fixes:** S3
- **Where:** `GuardCommand.cs` (`GitPrefix`, `Rules`, `ActiveRunGitMutation`, `Writes`, `Protected`) and `src/Hoshin.Workflow/Engine/WorkflowRunner.cs` (`EvaluateAndCommitTask`).
- **Problem:** the built binary allows:
  - `git -c alias.p=push p origin`
  - `git --git-dir .git --work-tree . push`
  - `g""it push`
  - `echo '{}' >.hoshin.json`
- **Do:**
  1. In `GitPrefix`, accept `--git-dir`/`--work-tree`/`--namespace`/`-C` with either a separate or `=` value.
  2. Deny any `git -c alias.*` and any `git config alias.*`.
  3. Normalize the command before matching: strip empty quote pairs (`""`, `''`) and backslash-newlines.
  4. In `Protected`, allow `>`, `>>` and `|` immediately before the path.
  5. **Library enforcement:** in `EvaluateAndCommitTask`, compare `repo.Head()` with the task's `WorkflowIssued.StartHead` (plus the verdict commits of earlier tasks). If HEAD moved by any commit the library didn't make, record a failed verdict (`reason: "git: HEAD moved outside the workflow"`) and commit nothing.
- **Test first:**
  - Guard tests asserting `deny` for the four commands above, plus `git commit -m x -n` (the `-n` after a valued option).
  - A runner test: issue T1, make a manual `git commit` in the repo, call `EvaluateAndCommitTask`, and assert the verdict failed with that reason and no new commits were made.
- **Done when:** these tests pass, and DESIGN.md says the regex guard is a speed bump while the library check is the real enforcement.

### T05 · Read build checks from the base branch, not the working tree  `security`

- **Fixes:** S6
- **Where:** `src/Hoshin.Workflow/Engine/WorkflowRepo.cs` (`ReadChecksFromHooks`, and the `ReadPlan` / `LoadPlanMetadataOnly` callers) and `src/Hoshin.Workflow/Events/HookConfig.cs` (`HookConfigService`).
- **Problem:** `build.finish` checks are read from `<change>/hooks.jsonc` or `hooks.json` in the **working tree**. An implement subagent can edit that file to drop or neuter checks. The two accepted shapes also default `blocking` differently.
- **Do:**
  1. Read the checks from the **base ref** (`change.jsonc` `base`): `.hoshin.json` hooks plus `intent/<system>/hooks.json`, using `HookConfigService` the way System A does. Map the `build.finish` blocking hooks to `WorkflowPlan.Checks`.
  2. If you keep supporting a per-change `hooks.jsonc` for the `.hoshin/` toy layout, read it at the base ref with `git show <base>:<path>`, never from disk.
  3. Use one `blocking` default for both shapes: blocking unless `false`.
- **Test first:** a runner test. Commit a `build.finish` check that fails on base. Then, in the working tree, rewrite the hooks file so it has no checks. Call `WorkflowState.Read` and assert `Plan.Checks` still contains the committed check.
- **Done when:** the test passes, the existing `RunChecks` tests pass, and DESIGN.md §3 says where checks come from.

### T06 · Protect the change folder and shell writes during a build  `security`

- **Fixes:** S7
- **Where:**
  - `WorkflowRunner.IsProtectedPath`;
  - `GuardCommand`: `CheckWhileWriting`, the red-phase check, and the `Implement`/`Retry` branch of `Evaluate`.
- **Problem:**
  - During `Implement`, the change's own `intent/<sys>/<change>/plan.md` can be edited (its task `test` argv rewritten), and the edit is committed as production code.
  - Red-phase and the single-document lock only inspect `Edit`/`Write`, so `cat > src/a.cs <<EOF` gets past them.
- **Do:**
  1. Have `IsProtectedPath` take the change folder, and treat everything under it as protected during `Implement`/`Retry`.
  2. Give the guard the same rule, using `CurrentStep.ChangeFolder`.
  3. For shell commands during `Implement`, `Retry` and the writing steps, extract redirect and `tee` targets (`>`, `>>`, `tee [-a] <path>`). Apply the same path rules to them as to `Edit`/`Write`.
- **Test first:**
  - A runner test: the agent's work modifies `plan.md` in the change folder, and the verdict fails with `protected:` and makes no commits.
  - Guard tests, during a red-phase `Implement` with no test file yet:
    - `cat > src/prod.mjs <<'EOF'` is denied;
    - during `WriteDocument(intent)`, `echo x > src/a.cs` is denied.
- **Done when:** the tests pass.

### T07 · Scope the workflow ledger to one change  `correctness`

- **Fixes:** E1
- **Where:** `src/Hoshin.Workflow/Engine/WorkflowContracts.cs` (`WorkflowLedger` and its records) and `WorkflowRepo` (`LedgerPath`, `StepPath`, `ReadLedger`, `WithLedger`).
- **Problem:** `.git/hoshin/workflow-ledger.json` is one file per repository, and verdicts are keyed only by task id. After change A's `T1` passes, a new change B whose first task is also `T1` is treated as already passed: `Next` skips straight to `T2` or `RunChecks`.
- **Do:**
  1. Store one ledger per change at `.git/hoshin/changes/<change>/workflow-ledger.json`, keyed by `WorkflowPlan.Change`. Keep `run.json`/`step.json` global, but record the change in them (they already carry `ChangeFolder`).
  2. On first read, if a legacy `.git/hoshin/workflow-ledger.json` exists and `run.json` names a change, move it there once. Otherwise leave it in place.
- **Test first:** in `WorkflowNextTests` or a runner test:
  1. Create change A with task `T1`, and record a passing verdict.
  2. Create change B, also with task `T1`, and switch the run to B.
  3. Assert that `Next(B)` is `Implement(T1)`.
- **Done when:** the test passes and DESIGN.md §2/§7 show the new path.

### T08 · Commit only the task's own paths  `correctness`

- **Fixes:** E2, E11
- **Where:** `WorkflowRunner`: `Issue`, `EvaluateAndCommitTask`, `GetWorkingTreeChanges` and `StagePaths`.
- **Problem:**
  - `GetWorkingTreeChanges` reads `git status --porcelain -uall` for the whole repo. Every non-test path becomes a production change.
  - A pre-existing untracked `notes.txt`, an editor swap file, or any file already staged before the attempt ends up in the `feat(...)` commit.
  - `git commit` has no pathspec.
  - The porcelain output is parsed without `-z`.
  - A failed first `StagePaths` leaves the index partly staged.
- **Do:**
  1. In `Issue`, record a snapshot of the dirty and untracked paths (with content hashes for tracked files) in `WorkflowIssued`. At evaluation, exclude paths unchanged since that snapshot.
  2. Refuse the attempt (failed verdict, `reason: "git: the index had staged changes before the attempt"`) if the index wasn't clean when the task was issued.
  3. Parse `git status --porcelain=v1 -z -uall`.
  4. Commit with an explicit pathspec: `git commit -m … -- <paths>`.
  5. On *any* failure after staging begins, run `git reset --mixed --quiet <startHead>`.
- **Test first:** in a runner test:
  1. Before issuing T1, create an untracked `notes.txt`, and stage an unrelated `docs/x.md`.
  2. After issuing T1, make valid test and production changes.
  3. Assert that `notes.txt` and `docs/x.md` appear in neither commit (`git show --name-only`).

  A second test uses a path with a space and a quote (`src/a "b".mjs`), and asserts it is committed correctly.
- **Done when:** the tests pass.

### T09 · Serialize evaluate-and-commit  `correctness` `confirm first`

- **Fixes:** E3
- **Where:** `WorkflowRunner.EvaluateAndCommitTask`, `WorkflowRepo.WithLedger`/`AcquireLock`, and `HookOrchestrator.JudgeAndAdvance`.
- **Problem:** the existing-verdict check reads `state.Ledger`, a snapshot taken before any lock. If `SubagentStop` and the parent `Stop` both reach `JudgeAndAdvance` for the same attempt, both run Red/Green, both commit, and both share the `worktrees/red-check` directory.
- **Do:**
  1. Add a run-level lock (for example `.git/hoshin/run.lock`, using the same mechanism as `AcquireLock`), held across the whole of `EvaluateAndCommitTask`.
  2. Inside the lock, re-read the ledger and return any existing verdict for `(change, task, attempt)` before doing work.
- **Test first:** call `EvaluateAndCommitTask` for the same task and attempt from two threads at once, on a fixture with valid work. Assert:
  - exactly one `test(...)` and one `feat(...)` commit;
  - both calls return the same verdict.

  The test may fail intermittently before the fix. Run it in a loop of 20 to be sure it fails.
- **Done when:** the test passes reliably (20 of 20).

### T10 · Make `migrate` fail closed  `data-loss`

- **Fixes:** L1, L2, L3 (the minimal part, independent of D3)
- **Where:** `src/Hoshin.Workflow/Migration/YamlToJsoncConverter.cs` (`Parser.ParseRoot`, `SplitInlineComment`, the flow-collection detection) and `DocumentMigrator.cs` (the `change.yml` branch that calls `File.Delete(file)`).
- **Problem:** `ParseRoot` never checks that it consumed every line, so the rest of the document is silently dropped. Verified: a `change.yml` with `notes: "first line\n  second line"` followed by `base:` and `branch:` was migrated with `--write`. The tool reported `done` and deleted the source, and `change.jsonc` lost `base` and `branch`. Also:
  - `title: It's a "test" # note` keeps the comment;
  - `title: [WIP] Fix` becomes `["WIP"]`.
- **Do:**
  1. After `ParseRoot`, if any non-blank, non-comment line wasn't consumed, throw with the line number. The item's status becomes `refused` with that message.
  2. Refuse, with a clear message, any input that contains:
     - anchors (`&x`), aliases (`*x`) or tags (`!!`);
     - a multi-line quoted scalar;
     - `- |` / `- >` inside sequences;
     - a nested sequence (`- - x`);
     - duplicate keys;
     - a leading `---` in `change.yml`.
  3. In `SplitInlineComment`, a quote starts a quoted region only when it is the *first* character of the value.
  4. Treat `[`/`{` as a flow collection only if the whole value parses as one. Otherwise refuse.
  5. Before deleting `change.yml` (or rewriting a document), parse the new JSONC, convert it back to the original's key set, and require every source key to be present. If any is missing, refuse and leave the source untouched.
- **Test first:** `DocumentMigrationTests`, one case per construct above. Each asserts that:
  - the status is `refused`;
  - the source file still exists and is byte-identical;
  - no target file was written.

  Plus the `title` cases, which must convert correctly or be refused, never mistranslated.
- **Done when:** the tests pass, and `migrate --dry-run` reports `refused` for these inputs.

### T12 · `Jsonc.TryParse` accepts empty and comment-only text  `correctness`

- **Fixes:** L4
- **Where:** `src/Hoshin.Workflow/Common/Jsonc.cs` (`TryParse`, `IsBlank`), plus the callers whose `value is null` branches never run (`FrontMatter`, `PlanReader`).
- **Problem:** `IsBlank` calls `Utf8JsonReader.Read()`, which throws on input with no tokens. The exception is caught and `false` returned, so `""`, whitespace, `// none yet` and `/* x */` all fail with "does not contain any JSON tokens". A plan's `## Shared decisions` block containing only `// none yet` is reported as invalid.
- **Do:** treat a `JsonException` thrown on the very first `Read()` with no tokens consumed as blank, or strip comments and whitespace first. Return `true` with `value = null` for blank input, as the documentation says.
- **Test first:** a unit test. `Jsonc.TryParse` returns `true` and `null` for `""`, `"  \n"`, `"// c"` and `"/* x */"`. Add a plan example under `tests/Hoshin.Tests/Examples/plan/2/` whose shared-decisions block contains only a comment, with an `expected.json` that has no problems.
- **Done when:** the tests pass and no other example changes its expected output. If one does, inspect it, and justify the change in the PR.

### T13 · Rewrite `FrontMatterEdit.Set` on real JSONC tokens  `correctness`

- **Fixes:** L5
- **Where:** `src/Hoshin.Workflow/Documents/FrontMatterTools.cs` (`FrontMatterEdit.Set`). Callers: `WorkflowRunner.CommitDocument` (the source pins) and `AcceptDocument` (status).
- **Problem:**
  - The regex only matches existing *string* values, so `"source_intent_commit": null` gets a duplicate key, and the pin reads back as null.
  - It rewrites matches inside `//` comments, so `// was "status": "draft"` is edited while the real `status` stays `draft`.
  - It converts CRLF to LF across the whole file.
  - Its write isn't atomic.
- **Do:**
  1. Find the front-matter fence via `Doc`.
  2. Tokenize the JSONC with `Utf8JsonReader`, with comments allowed, tracking byte offsets.
  3. Locate the top-level property by name, and replace its value span whatever the value's type.
  4. If the property is missing, insert it before the closing `}`, matching the existing indentation and trailing-comma style.
  5. Keep the file's line endings, and write via a temp file plus `File.Move`.
- **Test first:** unit tests covering:
  - an existing string value;
  - an existing `null` value;
  - a number value;
  - a missing key;
  - a key named inside a `//` comment above the real key;
  - a CRLF file.

  Each asserts that parsing the result yields exactly one key with the new value, comments are unchanged, and line endings are preserved.
- **Done when:** the tests pass and the authoring tests (`AuthoringAndReviewHookTests`) stay green.

---

## P1 — Correctness

### T11 · Replace the YAML converter (`Hoshin.Migration` + YamlDotNet)

- **Fixes:** L1–L3, fully
- **Depends on:** T10.
- **Do (D3 = (a)):**
  - Create `src/Hoshin.Migration` (referencing `Hoshin.Workflow` and YamlDotNet), and move `DocumentMigrator`, `IYamlToJsoncConverter` and the converter into it.
  - Implement the converter on YamlDotNet's representation model, with these rules:
    - keep key order;
    - keep comments where YamlDotNet exposes them, otherwise drop them and record a warning;
    - quote all-digit commit hashes.
  - `Hoshin.Cli` references the new project, and `Hoshin.Workflow.csproj` stays Markdig + Gherkin.
- **Test first:** a differential test over the 34 samples used in the review (rebuild them from ANALYSIS §5.3: block scalars, flow collections, quoting, escapes, numbers, anchors, comments). For each, run the original plugin's parser (`hoshin/tools/vendor/yaml.mjs`, via `node`), then the .NET converter, then `JSON.parse`. Assert the results are deep-equal, or that .NET refused.
- **Done when:** there are zero mismatches, and the T39 architecture test that pins `Hoshin.Workflow`'s references still passes.

### T14 · Harden `RedClassifier`

- **Fixes:** E4
- **Where:** `src/Hoshin.Workflow/Engine/RedClassifier.cs` (`Classify`) and `WorkflowRunner.ObserveRedOnCachedWorktree`.
- **Problem:** anything that doesn't match a C#/TypeScript syntax regex counts as valid Red. That includes:
  - exit 127 ("could not start …");
  - crashes;
  - other languages' compile errors (Python `SyntaxError` is handled; Go, Rust and Java are not);
  - `Cannot find module 'some-dependency'`, which is a missing dependency, not a missing target symbol.

  Separately, "could not prepare detached worktree" is reported as `RedKind.TimedOut`.
- **Do:**
  1. Add `RedKind.CouldNotRun`, which is never valid Red. Use it for exit codes 126 and 127, for `could not start`, and for worktree preparation failures.
  2. Count `Cannot find module` / `ERR_MODULE_NOT_FOUND` / `TS2307` as a missing symbol **only** when the module specifier is relative (`./`, `../`) or matches a path in the task's production changes. A bare package name is `CouldNotRun`.
  3. Add invalid-Red patterns for:
     - Go: `syntax error:`, `expected`;
     - Rust: `error: expected`, `error[E0`, apart from `E0425`/`E0433`/`E0412`, which are missing symbols;
     - Java: `error: ';' expected`.
- **Test first:** `Classify` unit cases (exit code and output → expected kind and validity):

  | Exit code | Output | Expected |
  | :--- | :--- | :--- |
  | 127 | `could not start x` | `CouldNotRun`, invalid |
  | 1 | `Error: Cannot find module 'vitest'` | `CouldNotRun`, invalid |
  | 1 | `Cannot find module '../src/a.mjs'` | `MissingSymbol`, valid |
  | 2 | Go `syntax error: unexpected }` | `SyntaxError`, invalid |
  | 101 | Rust `error[E0425]: cannot find value` | `MissingSymbol`, valid |
  | 101 | Rust `error: expected one of` | `SyntaxError`, invalid |

- **Done when:** the tests pass, and DESIGN.md §4's list matches the code.

### T15 · Fix test-path classification and give it unit tests

- **Fixes:** E5
- **Where:** `WorkflowRepo.IsTestPath`, `MatchesGlob` and `ExtractTestFilesFromArgv`.
- **Problem:**
  - Any argv token containing `.` or `/` is treated as a test file. So `dotnet test src/App/App.csproj` makes a production change to `App.csproj` count as a test.
  - `TrimStart('.')` turns `../x` into `x`, and `.config/x` into `config/x`.
  - `**/foo` matches `barfoo`.
- **Do:**
  1. `ExtractTestFilesFromArgv` returns only tokens that exist as files in the repo *and* match the configured test patterns, or that end in a known test-file suffix (`.test.*`, `.spec.*`, `*Tests.cs`, `*Test.cs`, `test_*.py`, `*_test.go`).
  2. Don't trim leading dots. Normalize with `Path.GetRelativePath(repoRoot, Path.GetFullPath(token, repoRoot))`, and drop tokens outside the repo.
  3. Translate `**/` to `(?:.*/)?`.
- **Test first:** table-driven unit tests for:
  - `IsTestPath` with the default patterns: `src/a.test.mjs` true, `src/App.csproj` false, `tests/x/y.cs` true, `foo/tests/z.cs` true, `latest/x.cs` false;
  - `MatchesGlob("**/foo", "barfoo")` false;
  - `ExtractTestFilesFromArgv(["dotnet","test","src/App/App.csproj"])` is empty.
- **Done when:** the tests pass and the existing runner tests still pass.

### T16 · Resolve relative executables against the working directory

- **Fixes:** E6
- **Where:** `src/Hoshin.Workflow/Engine/AllowListedCommandRunner.cs` (`Run`); do the same in `Git/CommandRunner.cs` for consistency.
- **Problem:** .NET resolves an `argv[0]` containing `/` against the hoshin process's cwd, not `WorkingDirectory`. Verified: a task `test: [".venv/bin/runtests", "test/t1.test.mjs"]` failed Green with "could not start … No such file or directory" when hoshin ran from another directory. In the Red worktree, the same argv would resolve to the *main* repo's file.
- **Do:** if `argv[0]` contains a directory separator and isn't rooted, replace it with `Path.GetFullPath(argv[0], workingDirectory)` before starting the process.
- **Test first:** a runner unit test. Create `<tmp>/bin/hello.sh` (or `.cmd` on Windows), then call `Run(<tmp>, ["bin/hello.sh"])` from a different `Environment.CurrentDirectory`, and assert `Ok`.
- **Done when:** the test passes on Linux and Windows.

### T17 · Keep the environment that push, proxies and toolchains need

- **Fixes:** E7
- **Where:** `AllowListedCommandRunner.AllowedEnvironmentVariables` and `WorkflowRunner.Publish`.
- **Problem:** the allow-list drops several kinds of variable:
  - `SSH_AUTH_SOCK` and `GIT_SSH_COMMAND`, so `git push` to an SSH remote fails;
  - `HTTP(S)_PROXY`, `NO_PROXY`, `SSL_CERT_FILE`, `SSL_CERT_DIR` and `NODE_EXTRA_CA_CERTS`, so restores and pushes behind a proxy fail;
  - `JAVA_HOME`, `GOPATH`, `GOROOT`, `CARGO_HOME`, `RUSTUP_HOME`, `VIRTUAL_ENV`, `PYTHONPATH`, `PNPM_HOME`, `NVM_DIR` and others, so toolchains break.

  `Publish` returns `null` with no reason when the push fails.
- **Do:**
  1. Add the proxy, CA and toolchain variables above to the allow-list. They aren't secrets.
  2. Use a separate allow-list for `Publish`'s git calls that also keeps `SSH_AUTH_SOCK`, `GIT_SSH_COMMAND`, `GIT_ASKPASS` and `GCM_*`. Task tests must still not see them.
  3. Read an optional `"env": { "allow": [ … ] }` from `.hoshin.json`, extending the task allow-list. Update `MarkerReader.MarkerFields` and CLIENTS.md.
  4. Have `Publish` return the failure output, and have the orchestrator show it.
- **Test first:**
  - With `HTTPS_PROXY=http://x` set, a task command prints `process.env.HTTPS_PROXY` and sees it.
  - `ANTHROPIC_API_KEY` and `GH_TOKEN` are still stripped. Extend `AllowListedCommandRunner_StripsSecretEnvironmentVariables`.
  - A `Publish` to a non-existent remote returns the git error text.
- **Done when:** the tests pass.

### T18 · Make `ProcessCommandRunner` timeouts hold

- **Fixes:** L6
- **Where:** `src/Hoshin.Workflow/Git/CommandRunner.cs` (`Run`, `RunAsync`).
- **Problem:** standard input is written synchronously *before* the timed wait, and output is read to end-of-file after exit. Verified:
  - `sleep 6` with 1 MB of stdin and a 1 s timeout took 6 s and reported `TimedOut=false`;
  - `sh -c "sleep 6 & echo"` with a 2 s timeout took 6 s.
- **Do:**
  1. Write stdin on a background task.
  2. Start the stdout and stderr reads before waiting.
  3. Wait on `process.WaitForExitAsync(cts.Token)` with the timeout.
  4. On timeout, `Kill(entireProcessTree: true)`.
  5. After exit, give the output reads a short grace period (2 s), then cancel them. A background grandchild must not hold the call open.
  6. Pass the `CancellationToken` through `RunAsync`.
- **Test first:** two tests:
  - `sleep 6` with 1 MB of stdin and a 1 s timeout returns within 3 s with `TimedOut=true`;
  - `sh -c "sleep 6 & echo hi"` with a 10 s timeout returns within 4 s with output `hi`.

  Skip-on-Windows variants are acceptable only for the `sh` case.
- **Done when:** the tests pass.

### T19 · Stop `LedgerStore` wiping a corrupt ledger; add a lock

- **Fixes:** L7
- **Where:** `src/Hoshin.Workflow/Events/Ledger.cs` (`LedgerStore.Read`, `Write`), plus the read-modify-write callers in `RedRecorder` and `EventRunner`.
- **Problem:** `Read` catches every exception and returns null. Callers then substitute `Empty()` and write it back, erasing runs, reds, approvals and gate-block counts. There is also no inter-process lock.
- **Do:**
  1. On a parse failure, throw `CouldNotRunException("<path> is not valid JSON: …")`, and never overwrite the file.
  2. Add `LedgerStore.Update(folder, repo, Func<RepoLedger, RepoLedger>)`, which holds a lock file (reuse `WorkflowRepo`'s mechanism; extract it into a shared `FileLock`) and writes atomically. Route all writers through it.
- **Test first:** write garbage to a ledger file, run `hoshin red` (or the relevant command) through the CLI, and assert:
  - exit code 2;
  - the garbage file is unchanged;
  - the error names the file.
- **Done when:** the test passes and the black-box suites stay green.

### T20 · `SpecReader` uses `Doc.Fences`; handle "Scenario Template"

- **Fixes:** L8, part of L12
- **Where:** `src/Hoshin.Workflow/Documents/SpecReader.cs` (`FindBlock`, and the outline check comparing `sc.Keyword.Trim() == "Scenario Outline"`).
- **Problem:**
  - `FindBlock` scans fences with its own regex, so a ` ```gherkin ` block nested inside a `~~~markdown` example is taken as the spec.
  - The migrator uses `Doc.Fences`, so the two disagree.
  - The Gherkin synonym `Scenario Template` skips the placeholder check.
- **Do:**
  1. Select the spec block from `Doc.Split(text).Fences`: the first top-level fence whose info string is `gherkin`.
  2. Detect outlines by the presence of `Examples` or the keyword type from the Gherkin AST, not by the keyword text.
- **Test first:** add two examples under `tests/Hoshin.Tests/Examples/spec/2/`:
  - `gherkin-inside-example-block.md`, where the real spec follows an example block;
  - `scenario-template-placeholder-missing.md`.

  Each gets an `expected.json` showing the correct requirements, or the placeholder problem.
- **Done when:** the examples pass.

### T22 · Claude Code `Stop` and `Result` act only for the run's session  `security`

- **Fixes:** S8
- **Where:** `HookOrchestrator.HandleStop`, `HandleResult` and `HandleSubagentStop` (today only Antigravity calls `IsSubagentSession`), and `GuardCommand.IsSessionInActiveRun`.
- **Problem:**
  - While session A builds, an unrelated session B in the same repo ends a turn. Its Stop is answered with "Use the Agent tool now … build T2".
  - B's Explore subagent is judged as T1's attempt.
  - In the guard, any event carrying `agent_id`, or any run with `step.AgentId` set, counts as in the run, whichever session it comes from. A missing `session_id` counts as in the run too.
- **Do:**
  1. Add `IsRunSession(repo, ev)`. It returns true if `ev.SessionId == run.SessionId`, or if `ev.AgentId` equals the registered `ActiveSubagent.AgentId` / `step.AgentId`.
  2. Have all three handlers return the harness's empty output for any other session.
  3. In the guard, use the same predicate, and treat a missing `session_id` as *not* in the run.
- **Existing test to change:** `DualHarnessHookIntegrationTests.ClaudeCode_ForegroundBuildRetryGuardChecksAndAcceptPublish_EndToEnd` lets a second session (`claude-s2`) accept and publish. Change it so that `claude-s2` gets no state change, and the original session accepts.
- **Test first:**
  - An unrelated session's Stop during `Implement` returns empty output and leaves `step.json` unchanged.
  - An unrelated session's `PostToolUse(Agent)` records no verdict.
  - The guard lets an unrelated session's `Edit` of a production file through during red-phase.
- **Done when:** the tests pass.

### T23 · `setup`/`context` don't take over the run

- **Fixes:** H2
- **Where:** `HookOrchestrator.HandleStart`. Today `WriteRunMarker` and `ResetStops` run before the `invoked is "setup" or "context"` early return.
- **Problem:** `/hoshin:context` overwrites `run.json` with the current session. The guard then applies a stale `Implement` `step.json` to that session, and Stop starts sending build briefs.
- **Do:**
  1. Move the `setup`/`context` early return above any run-marker, stop-counter or step write.
  2. Also let `setup`/`context` run when `ReadState` returns null (there is no plan yet). Today they are told "There is no change plan in this repository".
- **Test first:**
  - With an `Implement` step active for session A, session B starts `context`. Assert that `run.json` and `step.json` are unchanged, and that B's next Stop returns empty output.
  - In a repo with no `change.jsonc`, `setup` returns empty output, not the "no change plan" message.
- **Done when:** the tests pass.

### T24 · Judge only the dispatched implement subagent

- **Fixes:** H3
- **Where:** `HookOrchestrator.HandleResult`, plus the dispatch text built by `HookInstructions.For` / `WorkflowBriefs.Implement`.
- **Problem:** during `Implement`, any foreground `Agent`/`Task` tool result is judged as the task's attempt. An Explore or research agent burns attempt 1 with "no working-tree changes".
- **Do:**
  1. Embed a nonce in the dispatched prompt, for example `<!-- hoshin:T1:attempt-1:<8 hex> -->`, and store it in `step.json`.
  2. In `HandleResult`, judge only when `tool_input.prompt` (Claude) or the subagent prompt (Antigravity) contains the nonce. For any other subagent, return empty output and leave the attempt open.
- **Test first:** during `Implement(T1)`, send a `PostToolUse(Agent)` whose prompt lacks the nonce. Assert no verdict is recorded and `Next` is still `Implement(T1)` at attempt 1. Then send one with the nonce, and assert it is judged.
- **Done when:** the tests pass.

### T25 · Report verdicts and `NeedsApproval` in the Claude async flow

- **Fixes:** H5
- **Where:** `HookOrchestrator.HandleSubagentStop` and `HandleStop`.
- **Problem:** in Claude Code's background-subagent flow, `SubagentStop` judges and advances, but returns nothing. The parent's `Stop` then has no tracked agent id, so the verdict report is empty. When the last task passes, Stop reaches `NeedsApproval` and allows silently. The engineer never learns the outcome.
- **Do:**
  1. Have `HandleSubagentStop` queue the verdict report: write `pending-report.txt` under the run lock.
  2. On the parent's next `Stop`, drain and prepend it.
  3. At `NeedsApproval` or `Done`, if a report was drained, return `decision: block` once with the report plus "ready for approval: type /hoshin:accept" (or the failure outcome). Then allow on the following Stop.
- **Test first:** run the existing async scenario (`ClaudeCode_AsyncBackgroundSubagent_*`) through to the last task. Assert that the parent's Stop output contains the T1 verdict line, and the text "ready for approval".
- **Done when:** the test passes.

### T26 · Separate stop counters; honor `stop_hook_active`

- **Fixes:** H6
- **Where:** `HookOrchestrator`: `StopWhileWriting`, `CountStop`, `ResetStops` and `StopsPath`; also `NormalizedHookEvent.ContinuationActive`.
- **Problem:** one `stop-blocks` counter serves every purpose. During an interview with a partial draft on disk, each question turn blocks with diagnostics and increments the counter. After 3 turns the hook "gives up" and stays silent. `stop_hook_active` is parsed but never read.
- **Do:**
  1. Count only *consecutive* blocks within one continuation chain. Reset when `ContinuationActive` is false: a new user turn.
  2. While `WriteDocument` is active and the draft is invalid, don't block when the last assistant message ends with a question. Leave the engineer to answer.
  3. Write the counter under the run lock.
- **Test first:**
  - Three consecutive user-turn Stops during `WriteDocument(intent)`, each with an invalid partial draft and `stop_hook_active: false`, keep returning diagnostics; they don't give up.
  - Four chained Stops with `stop_hook_active: true` give up after 3.
- **Done when:** the tests pass.

### T27 · Truncate injected text without losing the closing marker

- **Fixes:** H7
- **Where:** `HookInstructions.For` and `HarnessAdapter.ContextOutput`/`BlockStopOutput` (the truncation helpers), and `WorkflowBriefs.MaxInjectedChars`.
- **Problem:**
  - The text is truncated to 10,000 characters, then a verdict report is prepended, and the result is truncated again from the end. That can drop the `END SUBAGENT PROMPT>>>` marker the model is told to copy up to.
  - `text[..n]` can split a surrogate pair.
- **Do:**
  1. Build the injected text in one place, with a fixed budget: header and report first, then the prompt body truncated in the *middle*, with a `[… N characters omitted …]` marker. The closing marker always stays.
  2. Truncate on a text-element boundary (`StringInfo`).
- **Test first:** a 20,000-character task text plus a 3,000-character verdict report. Assert that:
  - the output is ≤ 10,000 characters;
  - it ends with the closing marker line;
  - it contains the omission marker;
  - it holds valid UTF-16.

  Also a case where an emoji sits at the cut point.
- **Done when:** the tests pass.

### T28 · Make timeouts and the attempt cap configurable

- **Fixes:** E10
- **Where:**
  - `WorkflowEngine.MaxAttempts`;
  - `WorkflowRunner`: the hard-coded `TimeSpan.FromMinutes(5)` and `FromMinutes(10)`;
  - `WorkflowRepo.ReadPlan`;
  - `MarkerReader.MarkerFields`.
- **Do:**
  1. Read optional per-task `timeout` (seconds) in the plan task schema. Otherwise read `.hoshin.json` `build: { "testTimeoutSeconds", "checkTimeoutSeconds", "maxAttempts" }`. Fall back to the current defaults.
  2. Carry these values in `WorkflowPlan` (`Next` stays pure), and add them to `plan-task.schema.json` and CLIENTS.md.
- **Test first:**
  - A plan task with `"timeout": 1` running `node -e "setTimeout(()=>{},3000)"` fails Green with a timeout reason within 2 s.
  - With `maxAttempts: 1`, `Next` returns `Done(false)` after one failed verdict.
- **Done when:** the tests pass.

### T29 · Accept `testPatterns` in `.hoshin.json`

- **Fixes:** a doc/code mismatch (ANALYSIS §7)
- **Where:** `src/Hoshin.Workflow/Workspace/Marker.cs` (`MarkerFields`), `WorkflowRepo.ReadTestPatternsFromMarker`, DESIGN.md §4 and CLIENTS.md.
- **Problem:** DESIGN.md says test patterns are configured with `testPatterns` in `.hoshin.json`, and `WorkflowRepo` reads it. But `MarkerReader` rejects it as an unknown field, so `hoshin check .hoshin.json` fails on any marker that uses it.
- **Do:** add `testPatterns` (an array of strings) to `MarkerFields`, with validation (non-empty strings). Document it in CLIENTS.md.
- **Test first:** a black-box or unit test where a marker with `"testPatterns": ["spec/**"]` passes `hoshin check`, and the runner classifies `spec/a.rb` as a test.
- **Done when:** the test passes.

### T30 · The intent brief finds the system's context files

- **Fixes:** a parity gap (ANALYSIS §6)
- **Where:** `src/Hoshin.Workflow/Engine/WorkflowBriefs.cs` (`Intent`, which uses `Directory.GetFiles(state.ChangeFolder, "context*.md")`).
- **Problem:** context files live in `intent/<system>/context/<repo>.md`, not in the change folder, so the brief always says "no context files exist". The brief also writes the scope into `system`.
- **Do:**
  1. List `intent/<system>/context/*.md` (resolve the system from the change folder's parent).
  2. Keep the change-folder search for the `.hoshin/` layout.
  3. Use the real system name in the front-matter template.
- **Test first:** with a fixture containing `intent/demo/context/app.md` and a change under `intent/demo/<change>/`, assert that `WorkflowBriefs.Intent(state)` contains `intent/demo/context/app.md`.
- **Done when:** the test passes.

### T31 · Publishing: PR, review gate, branch default, diagnostics

- **Fixes:** E8
- **Where:**
  - `WorkflowRunner.Publish`;
  - `WorkflowEngine.Next` (the `NeedsApproval` → `Publish` path);
  - `WorkflowPlan.EffectiveBranch`;
  - `HookOrchestrator.Advance` (the `_runner.Publish(repo, state, p.Ref)` call).
- **Do (D4 as answered):**
  1. Make `EffectiveBranch` default to `change/<change>`. `change.jsonc` `branch` still overrides it.
  2. **Review gate in `Next`.** This keeps ordering in the engine, per ANALYSIS §3.3. After checks pass at the current head, `Next` returns `ReviewDocument` unless the ledger has a `WorkflowReviewRun` for the current **code head** with `HighCount == 0`. With high findings, it returns `Done(false, "review <n> has <k> high findings")`, or a fix step once A8 adds one.

     The **code head** is the latest commit that touches something outside `<change>/build/review.md`. That way, committing the review itself doesn't invalidate the review or the checks. Compute it in `WorkflowRepo`, and pass it in `WorkflowState` (`Next` stays pure).
  3. Key `WorkflowReviewRun`, `WorkflowCheckRun` and `WorkflowApproval` by code head, not raw head.
  4. Remove the orchestrator's own construction of `ReviewDocument`. `HookOrchestrator` now just runs what `Next` returns.
  5. `Publish` opens a PR against `base`, or reuses an open one (`IPullRequestClient.OpenPullRequest` / `OpenNew`), and records its URL.
  6. Surface push and PR failures to the engineer (see T17).
  7. Read the remote from the branch's upstream, falling back to `origin`, instead of always using `origin`.
- **Test first:**
  - `Next` after green checks, with no review, returns `ReviewDocument`.
  - With a review that has a high finding, it returns `Done(false, …)`.
  - With a clean review, it returns `NeedsApproval`.
  - Committing `build/review.md` after the review doesn't change the code head, so `Next` doesn't ask for checks or a review again.
  - `Publish` calls `IPullRequestClient.OpenNew` once with `base` and `change/<change>` (fake client), and reuses an existing PR when `OpenPullRequest` returns one.
- **Done when:** the tests pass, and DESIGN.md §3's state diagram and the README lifecycle diagram match.

### T41 · Tasks with no testable behavior

- **Fixes:** ANALYSIS §6 (parity), per D5
- **Where:**
  - `WorkflowRepo.ReadPlan`, which throws "has no test command";
  - `WorkflowContracts.WorkflowTask` (`Test` is non-null);
  - `WorkflowRunner.EvaluateAndCommitTask`;
  - `WorkflowBriefs.Implement`.
- **Problem:** the original allows "a task with no testable behavior (configuration, documentation)", which "has no `test` and no `compile`, and its 'done when' is the check". Hoshin.Net refuses to load such a plan. Even if it loaded, the runner would demand both test and production changes.
- **Do:**
  1. Make `WorkflowTask.Test` nullable.
  2. Move the task's evaluation behind an `ITaskVerdictPolicy` with two implementations (CONTRIBUTING O2), rather than adding branches to `EvaluateAndCommitTask`:
     - `RedGreenPolicy`: today's behavior.
     - `NoTestPolicy`, for tasks with `test: null`:
       - no Red or Green run;
       - at least one changed path;
       - none of the changes may be a test file (a test belongs to a task with a `test`);
       - one commit, `<type>(<scope>): <title>` with the trailers;
       - the verdict records `red: "skipped: no testable behavior"`.
  3. The brief tells the subagent that the task has no test, and that the build-finish checks prove it.
  4. `RunChecks` still runs after all tasks, so the checks are this task's "done when".
- **Test first:**
  - A plan with `T1` (`test: null`, changing `config/app.json`) loads.
  - Evaluating it with one config change passes, with one commit and no test commit.
  - With no changes, it fails with `work:`.
  - With a test-file change, it fails with `order: a task with no test must not change test files`.
- **Done when:** the tests pass, and DESIGN.md §4 and the plan section of CLIENTS.md (if it lists `test` as required) say a task's `test` may be null.

---

## P1 — SOLID conformance (required)

These fix existing violations of CONTRIBUTING.md §3. They are not optional, and each one shrinks the T39 baseline.

### T34 · Remove `?? Default` fallbacks; inject clock and file system

- **Fixes:** §8.5; CONTRIBUTING D1–D4
- **Depends on:** T39 (so the baseline shrinks visibly)
- **Do:**
  1. Make constructor dependencies required in `WorkflowRunner`, `HookOrchestrator`, `EventRunner`, `RedRecorder` and every class with `X? x = null` plus `x ?? X.Default` / `?? new X()`.
  2. Make `WorkflowRepo.Find` and `ReadPlan` take their runner and `PlanReader`.
  3. Delete `EventRunner.CreateDefaultSteps`; `HoshinServices` is the only composition root.
  4. Add `TimeProvider`, and use it instead of `DateTimeOffset.UtcNow`, in the engine and runner.
  5. Add a small `IFileSystem` (only the members actually used: read, write-atomic, exists, delete, enumerate), and use it in `WorkflowRepo`, `LedgerStore`, `FrontMatterEdit`, `ChangeRecordStore`, `MarkerReader` and `CrossChecker`. Turn the static classes among them into injected instances.
  6. Remove the production read of the test-only `HOSHIN_GIT_COUNT_FILE` from `ProcessGitClient`. Count git calls in tests with a decorating `IGitClient` instead.
  7. Keep the `static Default` properties only where a test or an external caller needs them. Grep the tests first.
- **Test first:** none (a pure refactor). The whole suite must stay green, and the PR must not change behavior.
- **Done when:** T39's D2, D3 and D4 baselines are empty, apart from documented NuGet-facing `Default` properties, and the suite is green.

### T35 · Give commands narrow dependencies; split fat interfaces

- **Fixes:** §8.4; CONTRIBUTING I1, I2
- **Depends on:** T34
- **Do:**
  1. Change each `IHoshinCommand` constructor to take only the services it uses. For example, `CheckCommand(IDocumentCheckService checker)`; extract interfaces for the concrete services a command needs.
  2. Have `HoshinServices` build lazily (`Lazy<T>`), or switch `Hoshin.Cli` to `Microsoft.Extensions.DependencyInjection`. That is allowed in the CLI, not in the library.
  3. Split `IGitClient` (34 members) into role interfaces of no more than 10 members each, along the lines of `IGitQuery` (rev-parse, show, ls-tree, log), `IGitStatus` (dirty and changed paths), `IGitCommitter` (add, commit, reset), `IGitBranches` and `IGitWorktrees`. `ProcessGitClient` may implement all of them, and each consumer takes only the ones it calls.
  4. Split `IWorkflowRunner` into `ITaskEvaluator`, `ICheckRunner`, `IApprovalRecorder`, `IPublisher` and `IDocumentCommitter`. `WorkflowRunner` may implement all of them at first.
- **Done when:** no command takes `HoshinServices`, T39's I1/I2 baselines are empty, and the tests pass.

### T36 · One owner for duplicated rules; enums for closed sets

- **Fixes:** E13, §8.6; CONTRIBUTING O1, R1
- **Do:**
  1. Delete `WorkflowBriefs.DefaultImplementProcedure`, and use the embedded `skills/implement/procedure.md`. Fail loudly if the resource is missing.
  2. Move the intent headings into one `IntentReader.RequiredHeadings` used by both the reader and the brief.
  3. Move the protected-path rules into one `ProtectedPaths` class in `Hoshin.Workflow`, used by both `WorkflowRunner` and `GuardCommand`.
  4. Move the tool-name lists into one `HarnessTools` class in `Hoshin.Cli/Hooks`, and generate the manifest matchers from it in `package.ps1`, or test that they match.
  5. Replace the string step kinds (`CurrentStep.Kind`) and the document names (`"intent"|"spec"|"plan"`) with enums. Use `JsonStringEnumConverter` so the JSON stays the same.
- **Test first:** a test asserting that the `matcher` values in both manifests equal the `HarnessTools` lists.
- **Done when:** the tests pass.

### T43 · Split `WorkflowRunner`, `WorkflowRepo` and `HookOrchestrator`

- **Fixes:** ANALYSIS §8.1; CONTRIBUTING S1, S3
- **Depends on:** T34. Do it after the P0 tasks that touch these classes have landed, to avoid conflicts.
- **Do:** pure moves first, then cleanup, with no behavior change. Targets:

  | Class | Split into |
  | :--- | :--- |
  | `WorkflowRepo` | `ChangeFolderResolver`; `WorkflowPlanLoader` (plan plus `change.jsonc`); `WorkflowLedgerStore` (read, write, lock); `TestPathClassifier` (globs, argv); a thin `WorkflowRepository` holding the paths. |
  | `WorkflowRunner` | `TaskEvaluator` (Red/Green, behind T41's `ITaskVerdictPolicy`); `RedWorktree` (cached worktree preparation and overlay); `WorkingTreeSnapshot` (T08); `CheckRunner`; `Publisher`; `DocumentCommitter` (intent, spec, plan, review, fix, accept); `BareFrontMatterWrapper`. |
  | `HookOrchestrator` | `SkillInvocationDetector`; `StartHandler`; `ResultHandler`; `StopHandler`; `TurnState` (stop counter, last start, pending context, under the run lock); a small `HookOrchestrator` that dispatches. |

  Each new type sits behind an interface where another type depends on it, and is wired in `HoshinServices`.
- **Test first:** none (a refactor). The full suite stays green, and you move the existing tests for each piece next to the piece.
- **Done when:** no file in `src/` is over 400 lines except baselined ones not touched here, and T39's S3 baseline has lost these three entries.

---

## P1 — Unify the two systems (D1 = a)

Goal: System B's engine and harness adapter, driven by System A's domain services, with one git layer, one ledger, one Red rule and one check configuration. Do A1–A8 in order. Each is one commit of no more than about 500 changed lines. If one would be larger, split it into further tasks (A4a, A4b, …), and record the split here.

### A1 · `WorkflowRunner` uses `IGitClient`

- **Depends on:** T35 (the role interfaces)
- **Where:** `WorkflowRunner`, `WorkflowRepo.Git`/`GitIn`/`GitWithEnv`, and `HoshinServices`.
- **Do:**
  1. Route every git call made by the engine, the runner and the orchestrator through the git role interfaces from T35.
  2. `WorkflowRepo` stops running processes.
  3. `IAllowListedCommandRunner` remains only for task tests, compiles and checks.
  4. Have `ProcessGitClient` use the allow-listed environment, plus the push allow-list (T17) for `push`. Repositories' own git hooks then don't receive model keys (S9).
- **Test first:** a `TaskEvaluator` unit test with fake git interfaces asserts the exact git operations for a passing attempt (stage the test paths, commit, stage the production paths, commit), with no process started.
- **Done when:** `grep -n '"git"' src/Hoshin.Workflow/Engine` finds no argv building, and the suite is green.

### A2 · One Red rule

- **Depends on:** A1, T14
- **Where:** `Red/RedRecorder.cs` (`hoshin red record`) and `Engine/RedClassifier.cs`.
- **Do:** `RedRecorder` classifies its run with `RedClassifier`, and maps the result onto the existing `outcome` values so CLIENTS.md keeps its fields:

  | `RedKind` | `outcome` |
  | :--- | :--- |
  | `AssertionFailure`, `MissingSymbol` | `failed` (red observed) |
  | `PassedUnexpectedly` | `passed` |
  | `SyntaxError` | `could-not-compile` |
  | `CouldNotRun`, `TimedOut` | `could-not-run` |

  Delete the separate exit-code rule. This changes what `failed` means for a missing-symbol compile error, so add a `CHANGELOG.md` entry and a note in `docs/KATA_MIGRATION.md`.
- **Test first:** one shared table of outputs, run through both `hoshin red record` (black-box) and `TaskEvaluator`, asserting the same verdict for every row.
- **Done when:** the table test passes.

### A3 · One ledger

- **Depends on:** A1, T07, T19
- **Do:**
  1. Fold `WorkflowLedger`'s records into System A's `RepoLedger`, at `.git/hoshin/<system>/<change>/<repo>.json`, locked (T19). The records are: issued, verdicts, check runs, reviews, approvals, published, the active subagent.
  2. Migrate a T07 per-change file once on first read.
  3. Delete `WorkflowLedger` and its store.
- **Test first:** after a hook-driven build of one task, `hoshin red show --json` and `hoshin status --json` (System A commands) report that task's verdict.
- **Done when:** one ledger type remains.

### A4 · `WorkflowState` from `ChangeStateService`

- **Depends on:** A1
- **Do:**
  1. Build `WorkflowState` from `ChangeStateService` (documents with pins, readiness, progress) instead of `WorkflowRepo.DocumentStatus`.
  2. Make `WorkflowEngine.Gate` refuse with `ReadinessMap`'s `SkillReadiness.Stop` reason when System A's `ready` would refuse. That covers stale pins, superseded plans, the light-track stop, and uncommitted acceptance.
  3. Keep `Next` pure: it receives the computed readiness as values.
- **Test first:** port three `state.test.mjs` refusals as `Gate` unit tests:
  - a plan whose `source_spec_commit` no longer matches the spec;
  - a superseded plan;
  - the light-track stop.

  Each `Gate` refusal must carry the same reason text as `hoshin state`.
- **Done when:** the tests pass, and DESIGN.md §3 says gating comes from `ChangeState`.

### A5 · The runner fires lifecycle events and writes build logs

- **Depends on:** A3, A4
- **Do:**
  1. Replace `WorkflowRunner.RunBaseline` / `RunChecks` with `EventRunner` events, with checks from `HookConfigService` (finishing T05's direction):
     - `build.start` (baseline);
     - `task.done` after each passing verdict;
     - `build.finish` (checks);
     - `review.finish` after `CommitReview`;
     - `change.done` for publishing, replacing `Publish` with `ChangeDoneStep`, which pushes every repository and opens PRs per GRAMMAR and D4.
  2. Append a `build/<repo>.md` entry per task (the `build-log-entry.schema.json` shape) with the verdict's `red`, and commit it with the implementation.
- **Test first:** a hook-driven build of a two-task change ends with:
  - `hoshin read --json build/<repo>.md` showing two entries with `red: "observed"`;
  - `hoshin checks --json` showing the `build.finish` events.
- **Done when:** the tests pass, and DESIGN.md's diagrams show events instead of direct runner calls.

### A6 · Multi-repo builds

- **Depends on:** A5
- **Do:**
  1. Add `Repo` to `WorkflowTask`; plan tasks already carry `repo` in `PlanReader`.
  2. Order tasks with `Changes/Schedule.cs`.
  3. Evaluate each task in its repository's checkout (resolved through `WorkspaceResolver`/`AppService`), on that repository's change branch.
  4. Keep the ledger per repository (A3).
  5. `Next` returns the next task across repositories in schedule order.
- **Test first:** a two-repository workspace fixture (`BlackBox/helpers/workspace.mjs` builds one), with `T1` in `app` and `T2` in `api`, gets commits in each repository's own history, and a build log per repository.
- **Done when:** the test passes.

### A7 · Start a change from `hook start`

- **Depends on:** A4
- **Fixes:** H1
- **Do:** when `intent` (or `fix`) is invoked and no change exists for the workspace, start one through `EventRunner` `change.start` (`ChangeStartStep`), the way `hoshin change start` does, then issue `WriteDocument(intent)`. Take the change name from the command arguments (`/hoshin:intent <name>`) or derive it with `Workspace/Naming`. Never pick an existing change by sort order: remove the guess in `ResolveChangeFolder` (ANALYSIS E9), and when several changes are open and none is named, ask.
- **Test first:**
  - In an empty workspace, `/hoshin:intent add-login` creates `intent/<system>/<date>-add-login/change.jsonc` and the change branch, and injects the intent brief.
  - With two open changes and no name, the hook asks which one, instead of picking.
- **Done when:** the tests pass.

### A8 · Reject, reopen and light-track accept flows; delete the unused half

- **Depends on:** A5, A7
- **Do:**
  1. Port the original's reject, reopen and `fix` accept flows (GRAMMAR, the `state`/`ready` and `fix-state` sections) into `Next` and `Gate` as engine steps.
  2. Then delete what is unused, and remove the T39 baseline entries that go with it:
     - `WorkflowRepo.ReadChecksFromHooks`;
     - the orchestrator's own step construction;
     - `GateCommand`, if no manifest wires it;
     - any remaining duplicate of a System A service.
- **Test first:** one test per flow, taken from the matching black-box cases (`fix-state.test.mjs`, `state.test.mjs`), driven through the hooks.
- **Done when:** ANALYSIS §3.2's table has one implementation per row, and DESIGN.md describes one system.

---

## P2 — Polish

### T21 · Small reader and validator fixes (batch)

- **Fixes:** L11, L12
- **Do:**
  1. `ReviewReader`: use `int.TryParse` for `## Review <n>`, and report a problem when it fails. Don't let it throw.
  2. `EmbeddedSchemaValidator`, `MarkerReader.NameRegex`/`CommitScopeRegex` and `ChangeRecordStore` SHA patterns: use `\z` instead of `$`, and add a match timeout for patterns that come from schemas.
  3. `EmbeddedSchemaValidator`: report unsupported keywords (`$ref`, `oneOf`, `anyOf`, `if`, `const`) as a validator error at load, instead of ignoring them.
  4. `CrossChecker.IsSuperseded`: use the parsed `PlanReadResult.FrontMatter` rather than re-reading from disk.
  5. `ChangeRecordStore`: invalid JSONC throws `CouldNotRunException`, rather than falling back to `change/<folder>`.
- **Test first:** one unit test per item.

### T37 · Fix false and stale doc claims

- **Fixes:** ANALYSIS §7
- **Do:** go through each row of ANALYSIS §7 and either correct the doc or link to the task that will make the claim true. Add a "Known limitations" section to README.md for whatever stays untrue after P0. Bring the relevant `GRAMMAR.md` sections into this repo as `docs/GRAMMAR.md`, or remove the code comments that cite it.
- **Done when:** every §7 row is resolved.

### T38 · Recorded harness payload fixtures

- **Fixes:** §5.2, §9
- **Do:**
  1. Capture real stdin payloads from Claude Code and Antigravity for each event the manifests use (a hook that tees stdin to a file), with paths and ids anonymized. Store them under `tests/Hoshin.Tests/Fixtures/hooks/<harness>/<event>.json`.
  2. Add a theory that runs `HarnessAdapter.Parse` on each one and asserts the normalized fields.
  3. Replace the hand-written payloads in the E2E tests with these fixtures where they cover the same event.
- **Done when:** every event and matcher in both manifests has a recorded fixture.

---

## Epics

### EPIC-B · Restore the dropped skill guidance, templates and evals

Moved to round 3, at the end of this file, as tasks B01–B15. Its evals are a reviewer-owned gate there (B02, B03), so a person still reviews every skill: through the cases they wrote, not by reading the procedure.

---

# Round 2 — close the lifecycle

Round 1 (everything above) is done. It landed as PR #5, and the second-pass review ([ANALYSIS.md](ANALYSIS.md)) found that a real change still can't get from intent to merged pull requests. Round 2 fixes that. Its IDs are `R00`–`R20`; the IDs in brackets (W4, E14, …) point to ANALYSIS.md.

Round 2 differs from round 1 in one way that matters: **it is gated by an acceptance test that the implementer did not write and may not change.**

## R-rules — how to work through round 2 (instructions for the agent)

Every rule of round 1's "How to work through this file" still applies, with these changes.

0. **Before the first task, read the gate.** Read the header comment of `tests/Hoshin.Tests/Acceptance/WorkspaceLifecycleAcceptanceTests.cs` and the message of the commit that added it (`git log -1 617291d`). The gate drives the shipped `hoshin.dll` with Claude Code hook payloads through one whole change, in a workspace whose master is `ai-sdlc`, with each repository built from a session started inside it. It judges only what an engineer can see.
1. **The gate belongs to the reviewer.** Don't edit, skip, filter, rename or re-trait anything under `tests/Hoshin.Tests/Acceptance/`, and don't change CI so that it stops running there. The engineer checks the diff of that folder before merging (CONTRIBUTING §2.1).
2. **When you think the gate is wrong,** stop work on that task. Add the failure message, including the hook transcript it prints, to the pull request description under a "Gate disputes" heading, and say why you think the test is at fault. The reviewer decides. Don't work around it in the meantime. Stages 4–9 of the gate have not yet run against working code, so a dispute there is possible and expected; raising one is part of the job.
3. **The gate is red until R01–R09 are done.** So the commit rule changes: every commit builds with 0 warnings and passes the fast suite *without the gate*:
   ```bash
   dotnet build Hoshin.sln
   dotnet test tests/Hoshin.Tests --filter "FullyQualifiedName!~BlackBoxConformanceTests&Category!=Acceptance"
   dotnet test tests/Hoshin.Tests --filter "Category=Acceptance"   # run at every commit too; record where it now fails
   ```
   The gate's first failure must never move backward. Each commit records it in a `Gate:` trailer (below).
4. **Each task still writes its own test first.** The gate shows that the whole lifecycle works; it doesn't replace the task's own test of its own behavior.
5. **A task is done when its "Done when" holds, not when the gate moves.** If the gate moves past the task's stage but the task's own test doesn't pass, the task isn't done.

### Delivery

- **Branch:** `fix/round-2`. It exists, and its first commits are the gate (`617291d`), the review docs and this part of TASKS.md. Don't rebase those away.
- **Scope of the branch:** R01–R09 (tier R1) only. They are what the gate needs. R10–R20 moved to round 3, which the engineer chose to deliver as one pull request after this branch merges.
- **Pull request:** a draft, opened by the engineer, titled `fix(workflow)!: close the workflow lifecycle`. Its description keeps a Tasks table (task, commit subject, status, where the gate fails after it) and the "Gate disputes" section.
- **Commits:** one per task, in index order, with round 1's trailers plus one:
  ```
  Task: R02
  Test-first: <test name> failed before the change: <one-line failure>
  Docs: <owner documents updated, or "none">
  Gate: <the gate's first failing assertion after this commit, or "passes">
  ```
- **Ready for review:** when all three gate tests pass on Linux and Windows in CI and R01–R09 are `done` or `invalid`. Then the branch merges by squash, as usual.
- **No bypass:** this branch is code, so CONTRIBUTING §2.1 gives it no bypass. It merges when the checks are green, not before.

## Decisions needed for round 2

### D6 — How does the engineer decline at approval?

Today the engineer can only approve (W3). **Options:** (a) `/hoshin:reject <reason>` at `NeedsApproval` records a declined approval with the reason, and the change goes back to the fix loop of R06, like a review finding; (b) declining means "don't approve", and the engineer then runs a fix build with instructions. **Recommendation:** (a). It keeps a record of why, and it reuses R06's loop. **Answer: (a), with its own command.** Decided by the engineer, 2026-11-26: a typed `/hoshin:decline <reason>`, not `reject`. Round 3's B15 makes `/hoshin:reject` close a change for good, and one command that means "fix this" or "close this" depending on the change's state is how a change gets closed by mistake. See R22.
## Round 2 index

| ID | Tier | Title | Fixes | Needs | Gate stage | Status |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| R00 | R0 | End-to-end acceptance gate | §12.1 | — | — | done (`617291d`) |
| R01 | R1 | A hook crash reaches the session | W14 | — | all (diagnosis) | done |
| R02 | R1 | Every hook finds the run and the change's checkout the same way | W11, W12, W13 | R01 | 1 | done |
| R03 | R1 | One workspace locator; sessions anywhere; a session builds only its repository | W4, W5, W13 | R02 | 2, 3 | done |
| R04 | R1 | Red runs `compile`, and sees the repository's gitignored tools | E14, E15 | — | 2, 3 | done |
| R05 | R1 | Progress comes from the committed build logs | W6 | R02 | — (own test) | done |
| R06 | R1 | A fix loop after checks, review and approval | W3 | R02, D6 for the approval part | 4, 5, 6 | done for checks & review; the approval part moved to round 3 as R22 |
| R07 | R1 | Publishing reaches `Done` and stays there | W1 | R02 | 8, 9 | done |
| R08 | R1 | A parked build traps no session and can be resumed | W2 | R02 | companion 2 | done |
| R09 | R1 | The guard sees every shell and write tool | S11 | — | companion 3 | done |
| R10–R20 | R2, R3 | Integrity, data, enforcement and parity | — | — | — | moved to round 3 |

The findings this round left without a task (W10, S14–S17, E16–E21, L18, C4) now have round-3 tasks, apart from the ones round 3 lists under "Accepted differences". "Gate stage" names the numbered comment in `WholeChange_…` that the task should get the gate past, or the companion test it should turn green. "Gate stage" names the numbered comment in `WholeChange_…` that the task should get the gate past, or the companion test it should turn green.

## R1 — make the normal path work

### R01 · A hook crash reaches the session

- **Fixes:** W14
- **Where:** `src/Hoshin.Cli/Commands/HookCommand.cs` (`RunAsync`, its `catch (Exception e)`), `HarnessAdapter` (`AppendTrace`, the output builders).
- **Problem:** every hook exception becomes one stderr line and exit 0 with empty output. The model and the engineer see nothing, and `hooks.jsonl` records nothing. W12 was invisible because of this.
- **Do:** keep exit 0, since a failing hook breaks the session. But for `start`, `result` and `stop`, emit the harness's normal context or Stop output with a short message: "hoshin hit an internal error in `<verb>`: `<exception type>: <message>`. Tell the engineer; don't retry." Append a trace line with the exception type and message, and keep the stderr line. Don't block Stop on an internal error; inform and allow, or a crash would trap the session.
- **Test first:** a unit test that makes the orchestrator throw (inject a failing `IWorkflowRunner`) and asserts that the Claude Code `hook result` output carries the message, the trace line exists, and the exit code is 0. Do the same for Antigravity's output shape.
- **Done when:** the test passes, and the gate's transcripts show the message instead of silence wherever a hook throws. The gate already fails on any `hoshin hook:` stderr line, so this task will make it fail *more clearly*, not less.

### R02 · Every hook finds the run and the change's checkout the same way

- **Fixes:** W11, W12, W13
- **Where:** `WorkflowAdvancer.ResolveRepo` / `ResolveRepoAt`, `StartChangeResolver.TryResolveOrStart`, `ResultHandler`, `StopHandler`, `GuardCommand.Evaluate`, `WorkflowRepo.Find`, `ChangeFolderResolver`, `WorkflowStateReader.Read`.
- **Problem:** `hook start` re-roots a run onto the change's copy (`<workspace>/.hoshin/copies/<change>`), but `hook result`, `hook stop` and `guard` resolve the repository from the session's `cwd`. So a workspace intent draft is never committed (W11). A change in a copy crashes the result hook on a null `ReadState` (W12). A product-repository session's hooks look in that repository's `.git/hoshin` and see no run (W13).
- **Do:**
  1. Put run resolution in one injected type, used by every hook verb and the guard. Given a hook event, it finds the active run for the session from the *workspace*: the master's common git dir, which a copy shares, plus product repositories. It returns the checkout that holds the change folder (the copy, when there is one) and the repository the session is in. Delete the per-handler `ResolveRepo(ev)` calls.
  2. `JudgeAndAdvance` must not use `!` on `ReadState`. A null state is a reported error (R01), not a crash.
  3. Write `run.json` in one place that every repository of the workspace resolves to, and say where in DESIGN.md.
- **Test first:** in a workspace fixture created with `hoshin change start` (a real copy, not a change folder in the master's main checkout):
  - `/hoshin:intent`, write the draft where the brief says, Stop → the draft is committed in the copy;
  - with an accepted plan seeded in the copy, `/hoshin:build` from the master, then the subagent's result → T1 is judged and committed;
  - `guard` in a product-repository session during that build denies `git commit`.
- **Done when:** those tests pass, `MultiRepoBuildTests` also runs with a change created by `change start`, and the gate gets past stage 1. `Gate:` names the new first failure.

### R03 · One workspace locator; sessions anywhere; a session builds only its repository

- **Fixes:** W4, W5, W13 (the product-repository half)
- **Where:** the four `HasWorkspaceMarker` copies (`ChangeStarter`, `TaskRepoResolver`, `WorkflowEventBridge`, `WorkflowStateReader`), `ChangeLedgerPaths.ResolveSystemAndChange`, `WorkflowEngine.NextScheduledTaskStep`, `IWorkspaceResolver`.
- **Problem:** workspace detection looks for a sibling folder literally named `master`, in four copies, so a session in a product repository gets "There is no change plan" (W4). A hook-started change can resolve to a different ledger key on the next hook (W5). The original builds each repository from a session started inside it, so that repository's `CLAUDE.md`, skills and hooks are loaded (hoshin `skills/build/SKILL.md` lines 25 and 28). Hoshin.Net issues any repository's next task from any session.
- **Do:**
  1. One `WorkspaceLocator`, injected, that finds the master by its `.hoshin.json` marker through `WorkspaceResolver`, whatever its folder name. Delete the four copies and the `"master"` literal they use (CONTRIBUTING R1).
  2. Ledger coordinates come from the change folder's `intent/<system>/<change>` path, never from `scope`.
  3. A build in a session started inside a product repository issues only that repository's tasks. When it has none left, it tells the engineer what comes next (the next repository's `build` command, or `review`), as the original's `schedule` does, and lets the session end. A build from the master session may keep today's behavior.
- **Test first:** with the master folder named `ai-sdlc`:
  - `/hoshin:build` from `api` dispatches only api's task;
  - after it, Stop in the api session is silent, and the context named web's build as the next step;
  - `hoshin state` and the hooks agree on the ledger path for a hook-started change.
- **Done when:** the tests pass, `grep '"master"'` finds no workspace-detection literal, and the gate gets past the dispatch half of stages 2 and 3.

### R04 · Red runs `compile`, and sees the repository's gitignored tools

- **Fixes:** E14, E15
- **Where:** `RedWorktree.ObserveRed`, `RedGreenPolicy.EvaluateAndCommit`, `IGitWorktrees.EnsureDetachedWorktree`, `ProcessExecutable.Resolve`.
- **Problem:** the detached Red worktree runs only the test command, never `compile`, so a test that needs build output fails for the wrong reason (E14). Gitignored tools (`node_modules/…`, `.venv/bin/…`) aren't in the worktree, so their tasks are classified `CouldNotRun` and can never pass (E15). The gate's T1 has both a `compile` step and gitignored build output; T2 runs `node node_modules/testkit/run.mjs`.
- **Do:**
  1. Run the task's `compile` in the Red worktree before the test. A compile failure caused only by missing target symbols is valid Red; any other compile failure is not.
  2. Make the main checkout's gitignored dependency folders visible in the worktree without copying them: link (`node_modules`, `.venv`, and folders listed in a marker option) or resolve argv paths against the main checkout. Pick one and document it in DESIGN.md. Never commit anything from them.
- **Test first:** two real-process tests (no fake runner): a Node task whose test imports `../dist/x.mjs`, with `compile: ["node", "build.mjs"]` and `dist/` gitignored, gives valid Red and then passes; a task whose test command is `node node_modules/<tool>/run.mjs …` with `node_modules/` gitignored gives valid Red and passes.
- **Done when:** both tests pass on Linux and Windows, and the gate's T1 and T2 are accepted on their first attempt (stages 2 and 3 pass).

### R05 · Progress comes from the committed build logs

- **Fixes:** W6
- **Where:** `WorkflowState.VerdictsForTask`, `WorkflowEngine.NextScheduledTaskStep`, `WorkflowStateReader`, `ChangeStateService`.
- **Problem:** the hook path reads progress from the local ledger only. A build continued on another machine, from a fresh clone, or after the TypeScript plugin re-issues tasks that are already logged and committed.
- **Do:** a task is done when its build-log entry is committed (as `ChangeState` already decides). The ledger becomes a cache of attempts and verdict details, not the source of truth. If the ledger and the logs disagree, the logs win and the disagreement is reported once.
- **Test first:** with T1 logged in a committed `build/app.md` and an empty `.git/hoshin` (a fresh clone of the master and the product repository), `/hoshin:build` issues T2, not T1, and `hoshin state` agrees.
- **Done when:** the test passes. The gate doesn't cover this; the reviewer will add a gate test for resuming on another machine once this lands.

### R06 · A fix loop after checks, review and approval

- **Fixes:** W3
- **Needs:** D6 for the approval part only. Do the checks and review parts first.
- **Where:** `WorkflowEngine.NextPostBuild`, `CheckRunner`, `StartHandler.ApplyHumanAcceptanceIfNeeded`, `GateCommand` (which already implements `gateRetries` for the CLI path).
- **Problem:** failed checks, a review with any high finding, and a declined approval all end in `Done(false)`. There is no step that lets anyone fix the change. In a multi-repo change, a fix in a product repository doesn't even move the master-only code head. The original handles this with `onFailure: fix` and `gateRetries` for checks, and with a build run that takes fix instructions after a review (hoshin `skills/review/SKILL.md` line 111).
- **Do:**
  1. Checks: a failed `build.finish` check with `onFailure: fix` returns a fix step to the agent, bounded by `gateRetries`, as `GateCommand` does. Only after the retries are used up is it `Done(false)`.
  2. Review: after a review with high findings, `/hoshin:build <instructions>` from inside the repository dispatches a fix subagent with the findings and the instructions. Its commits move that repository's head, and the next step is checks, then review again. Lift "code head" to the set of all touched repositories' heads, so a fix in any repository counts.
  3. Approval: per D6.
- **Test first:** a real-process test where review 1 has one high finding in a product repository, a `/hoshin:build fix F1 …` from inside it commits a fix, and the next step is review 2, then `NeedsApproval`. Another, where a failing check with `onFailure: fix` gives a fix step, then passes on the retry.
- **Done when:** both pass, and the gate gets past stages 4, 5 and 6.

### R07 · Publishing reaches `Done` and stays there

- **Fixes:** W1
- **Where:** `Publisher.Publish`, `ChangeDoneStep`, `WorkflowEngine.NextPostBuild`, `WorkflowState.EffectiveCodeHead`.
- **Problem:** `Publisher` records the pre-publish code head, and `change.done` then commits `change.jsonc`, which moves it. `Next` goes back to `RunChecks`, `Review` and `NeedsApproval`, forever.
- **Do:** record the head after `change.done`'s own commit, or leave `change.jsonc` out of the code head, the way `review.md` is. Then make every later hook in any session of the change quiet once `Next` is `Done(true)`.
- **Test first:** after a successful workspace publish, `Next` is `Done(true)`; a Stop in the master session is silent; `/hoshin:build` from a product repository dispatches nothing and opens no new pull request.
- **Done when:** the test passes, and the gate's stages 8 and 9 pass. At that point the whole `WholeChange_…` test should be green.

### R08 · A parked build traps no session and can be resumed

- **Fixes:** W2
- **Where:** `StopHandler.HandleAdvancedStepStop` (the parking branch), `GuardTamperingProtection.DetectMissingRunMarkerTampering`, `StopCounterStore`.
- **Problem:** after three ignored Stops, the build parks by deleting `run.json`. From then on, the tampering check blocks every Stop in the repository, from every session, forever. It checks neither the session nor `stop_hook_active`.
- **Do:** parking records that the run is parked (it doesn't delete the evidence that the tampering check reads as an attack). The tampering check applies only to the run's own session, and honors `stop_hook_active`. `/hoshin:build` from a new session resumes a parked run.
- **Test first:** park a build with four Stops; a Stop from an unrelated session is silent; a new session's `/hoshin:build` dispatches the task again and it completes.
- **Done when:** the test passes, and the gate's `InterruptedBuild_…` test passes.

### R09 · The guard sees every shell and write tool

- **Fixes:** S11
- **Where:** `packaging/claude-code/hooks/hooks.json` (the `PreToolUse` matcher), `HarnessTools` (`ClaudeShellTools` and the write-tool list), `GuardCommand`.
- **Problem:** the matcher is `Bash|Edit|Write|MultiEdit|NotebookEdit`, and `ClaudeShellTools` is only `Bash`. On Windows, `git push` through the PowerShell tool is never checked. MCP write tools aren't checked either.
- **Do:** add `PowerShell` to the matcher and to the shell tools, with the same command checks as `Bash`. Route the MCP tools that write files or push (`mcp__*` with a write or push verb) to the guard, and deny them during a build. The matcher and `HarnessTools` must come from one definition (CONTRIBUTING R1), with a test that keeps them in sync.
- **Test first:** `guard` with `tool_name: PowerShell` and `git push origin HEAD:refs/heads/main` during an active build is denied; the shipped `hooks.json` matcher contains `PowerShell`.
- **Done when:** the test passes, and the gate's `ActiveBuild_…` test passes.

## R2 and R3 — moved to round 3

R10–R20 are no longer delivered in later round-2 branches. The engineer moved them into round 3 (below), with full write-ups, to be delivered in one pull request.

---



# Round 3 — ready to replace hoshin

Round 2 closed the normal path: the R0 gate passes on Linux and Windows (PR #6). Round 3 is everything else between Hoshin.Net and replacing hoshin:
- integrity and data: R10–R16, plus the deferred findings a real user hits (R22–R30);
- enforcement: R17, R18;
- parity, which is mostly the judgment layer: EPIC-B, now tasks B01–B15;
- release: R20, then a real change run by the engineer (R40).

The engineer chose to deliver all of it in one round, on one branch. The IDs in brackets (L13, E18, …) point to ANALYSIS.md.

**Round 3 differs from rounds 1 and 2 in what "done" means.** Round 1 ended when every task's own test passed, and a change still couldn't finish (ANALYSIS §12.1). Round 2 ended when a gate written by the reviewer passed. Round 3 ends when Hoshin.Net meets the replacement bar below, and the implementer wrote no part of that bar.

## The replacement bar

Hoshin.Net is ready to replace hoshin when all five hold:

1. **Acceptance:** every test under `tests/Hoshin.Tests/Acceptance/` passes on Linux and Windows in CI, including the ones R21 adds.
2. **Evals:** every case under `evals/` passes against the built Claude Code bundle in at least 2 of 3 runs, in one recorded run under `evals/results/` (B02, B03).
3. **Parity:** every row of ANALYSIS §9 reads "None" or "Low", or names a difference listed under "Accepted differences" below.
4. **Dogfood:** one real change, not a fixture, has gone from intent to merged pull requests through the release candidate, in a real workspace with a master and at least one product repository (R40).
5. **Release:** a tagged release and a marketplace entry exist (D8), and the docs make none of the claims ANALYSIS §11 lists as false (R20).

The engineer runs item 4, not an agent, and it goes last. A defect it finds gets a gate test before it gets a fix (R3-rule 6).

### Accepted differences

These stay as they are. They are not on the bar, and a task must not spend effort on them. The engineer can move any of them into the round by adding a task.

| Item | Why it is off the bar |
| :--- | :--- |
| Documents are JSONC layout 2, not YAML | By design (AGENTS.md invariant 4). R10 makes the move safe for in-flight work. |
| Antigravity support, the NuGet library, `test: null` tasks, configurable caps | Hoshin.Net does more than the original here (ANALYSIS §9, last list). |
| H9, H10 (Antigravity queued context and manifest) | The original has no Antigravity support, so these aren't parity. They stay open in ANALYSIS. |
| S3, S4, S5, S7 remaining regex escapes, S14, S15 | The guard is a speed bump (ANALYSIS §6, "The lesson"); the original's guard is narrower still. R13 moves the checks that matter into the library, and R16 narrows the guard. |
| L10, L18, and the §10.2/§10.3 practice notes (async, file sizes, `File.*` calls) | Code health, not behavior a user sees. R17 stops them from growing. |
| Advance acceptance ("I accept the fix in advance") | Acceptance is typed-only by design (S1, round 1). The original's eval cases that accept in advance are graded up to the typed step, and B03's parity tests cover the rest with typed payloads. |
| No `--master` option on `build` | The master is found by its marker (R03). B11 keeps the safety half: a second clone of the master is never taken as the master. |

## R3-rules — how to work through round 3 (instructions for the agent)

Every R-rule of round 2 still applies, with these changes.

1. **Gates first, written by someone else.** R21 (new acceptance tests), B02 (the eval runner) and B03 (the eval cases) are written before any task they gate, and not by the session that implements those tasks. A separate session counts only if it has not seen the implementation. They land in their own commits at the start of the branch, the way `617291d` did in round 2. **Who, decided by the engineer, 2026-11-26:** the reviewer's session that wrote round 3 writes the gates. A different session implements every other task, and it never writes under `tests/Hoshin.Tests/Acceptance/` or `evals/`.
2. **The gates belong to the reviewer.** Don't edit, skip, filter, rename or re-trait anything under `tests/Hoshin.Tests/Acceptance/` or `evals/` (except new run records under `evals/results/`), and don't change CI or the eval runner so that it stops running them. When you think a gate is wrong, add it under "Gate disputes" in the pull request, as in round 2.
3. **Skills are judged by evals, not by line count.** A skill task (B05–B15) is done when its eval cases pass and every judgment rule its task lists is covered. Its length doesn't matter. Much of the original's skill text drives `hoshin.ts`. In Hoshin.Net the engine does that work, so porting that text would be wrong, and the strict skill lint (AGENTS.md invariant 5) refuses it anyway.
4. **The commit gate.** Every commit builds with 0 warnings and passes the fast suite and the acceptance folder. The exception is acceptance tests R21 added for tasks not yet done. The `Gate:` trailer names the first failing acceptance test, and it never moves backward. Evals cost too much to run on every commit, so each skill task runs its own cases and records the result in an `Evals:` trailer, for example `Evals: intent-keeps-scope-as-typed 3/3, intent-rejects-a-change 2/3`.
5. **Order.** Work through the tiers in order: R3-0 gates, R3-1 lifecycle and integrity, R3-2 enforcement, R3-3 parity, R3-4 release, then R40. Skills come after the engine tasks because the evals drive the real hooks. An eval that fails because of an engine defect says nothing about the skill.
6. **A defect found late gets a gate first.** If R40 or an eval exposes an engine defect, the reviewer adds a failing acceptance test for it, and then it is fixed as `R-new-<n>`, added at the end of the index.

### Delivery

- **Branch:** `fix/round-3`, from `main` after PR #6 merges. Its first commits are R21, B02 and B03.
- **Pull request:** a draft, opened by the engineer, titled `feat(workflow)!: ready to replace hoshin`. Its description keeps the Tasks table (task, commit subject, status, gate, evals), a "Gate disputes" section and a "Replacement bar" checklist with the five items above.
- **Commits:** one per task, in index order, with round 2's trailers plus `Evals:` on skill tasks:
  ```
  Task: B05
  Test-first: <test or eval case> failed before the change: <one-line failure>
  Docs: <owner documents updated, or "none">
  Gate: <the first failing acceptance test after this commit, or "passes">
  Evals: <case n/3, …>
  ```
- **Trailer check:** the conventional workflow checks `Task:` trailers only on `fix/review-findings`, and only for `T`/`A`/`EPIC` IDs. Before the first task commit, the engineer extends it to `fix/round-3` with `R` and `B` IDs, and makes it require `Gate:`, plus `Evals:` on B05–B15.
- **Release candidate:** when bar items 1–3 and 5 hold, R20 tags a release candidate from the branch, and the engineer runs R40 with it.
- **Ready for review:** when all five bar items hold and every task is `done` or `invalid`. It merges by squash, as usual, and R20 then tags the final release from `main`.
- **No bypass:** CONTRIBUTING §2.1 gives code no bypass. It merges when the checks are green.

## Decisions needed for round 3

D6 (decline at approval) is answered above: `/hoshin:decline <reason>`. R22 builds it.

### D8 — Under what name does Hoshin.Net ship?

(D7 was recorded and then withdrawn in round 2, so this one is D8.)

R20 was written to publish under a name that doesn't collide with the original, so both can be installed side by side. **Options:** (a) ship as `hoshin`, the next major version, from the original's marketplace, replacing it: the commands stay `/hoshin:*`, workspace instructions stay valid, and users upgrade, then run `hoshin migrate` (R10); (b) ship under a new name, in its own marketplace, next to the original. **Recommendation:** (a). With both plugins enabled, both sets of hooks fire on every event: two guards, two Stop gates and two engines committing to the same repository. So side by side isn't safe anyway, and once R10 makes `migrate` keep in-flight changes valid, the reason for it goes away. **Answer: (a).** Decided by the engineer, 2026-11-26: Hoshin.Net ships as `hoshin`, the next major version, from the original's marketplace, and replaces it. See R20.

### D9 — How do setup and context reach their mechanism?

Today `hook start` returns nothing for setup and context (H2 kept them from taking over a run), and the strict skill lint forbids a procedure from calling the CLI. So in the plugin, neither skill can read `source_commit`, compute what changed, or commit what it wrote. **Options:** (a) give each one a hook-issued step like the other skills: the engine gathers the data into the brief, and at Stop the library checks and commits what the skill wrote, never touching another change's run; (b) exempt setup and context from AGENTS.md invariant 5, and let their procedures call `hoshin` commands. **Recommendation:** (a). Every other skill already works this way, so the guard, the commit rules and the trace stay uniform, and invariant 5 keeps no exception. (b) is less work, but these would become the only skills that commit for themselves. B08 and B09 need this. **Answer: (a).** Decided by the engineer, 2026-11-26: setup and context get hook-issued steps, and invariant 5 keeps no exception.

## Round 3 index

"Gate" names the R21 acceptance test (by its number in R21) or the eval cases the task must turn green. Every task also has its own test, written first.

| ID | Tier | Title | Fixes | Needs | Gate | Status |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| R21 | R3-0 | Extend the acceptance gate (reviewer) | §12.2 | — | — | done (`82fb3d4`) |
| B02 | R3-0 | An eval runner for the Claude Code bundle (reviewer) | EPIC-B | — | — | written (`f11fd78`, `5d47acd`), not yet run |
| B03 | R3-0 | Port the eval suite (reviewer) | EPIC-B | B02 | — | sorting proposed in `evals/README.md`, waiting for the engineer's approval |
| R23 | R3-1 | One session's hand-off doesn't end another session's run | S16, R03 × R07 | — | R21 (10) | open |
| R22 | R3-1 | The engineer can decline at approval | W3 | D6 | R21 (2) | open |
| R10 | R3-1 | `migrate` keeps in-flight changes valid | L13–L16, L1 | — | R21 (3) | open |
| R11 | R3-1 | One stray commit doesn't burn every attempt | W7, H11, S17 | — | R21 (8) | open |
| R12 | R3-1 | Honor event refusals; fail loudly on an unresolved repository | W8, W9 | — | R21 (7) | open |
| R13 | R3-1 | Pin the base ref; authenticate approvals | S12, S13, S1, S6 | — | own test | open |
| R14 | R3-1 | Event hooks and `red record` get the allow-listed environment | S9 | — | own test | open |
| R15 | R3-1 | Retry atomic replace on Windows sharing violations | L17 | — | own test | open |
| R16 | R3-1 | Narrow the guard; cover MCP tools that run commands | S18, R09's gap | — | own test | open |
| R24 | R3-1 | Commit only what the task changed; check Green on a clean HEAD | E16, E17, E21, E12 | — | own test | open |
| R25 | R3-1 | Find co-located tests by default | E18 | — | R21 (9) | open |
| R26 | R3-1 | The Red classifier stops misfiring | E19, E4 | — | own test | open |
| R27 | R3-1 | Keep the engineer's git identity and signing | E20, E7 | — | own test | open |
| R28 | R3-1 | One judge per turn; refuse conflicting launches | W10, H4 | — | own test | open |
| R29 | R3-1 | Fail closed on bad input | S10, C3's two catches | — | own test | open |
| R30 | R3-1 | Readers agree with the migrator | L8, L12, L9 | — | own test | open |
| R17 | R3-2 | Make the baseline shrink-only, count every suppression, close the scanner holes | C1–C4 | R29 | own test | open |
| R18 | R3-2 | Run the `yaml.mjs` differential in CI; record real harness payloads | §8, §10.4, §9 black-box | — | own test | open |
| B01 | R3-3 | Templates and shared references | §9 "Templates" | — | `ignores-system-plan-template` | open |
| B04 | R3-3 | Skill descriptions that trigger | §9 "Skills" | — | every `skill-fired` grader | open |
| B05 | R3-3 | Start and triage | §9 "Start / triage" | B01 | R21 (4); `start-*` | open |
| B06 | R3-3 | Intent | §9 "Skills" | B01 | `intent-*` | open |
| B07 | R3-3 | Spec | §9 "Skills", "Lifecycle events" | B01 | `spec-*` | open |
| B08 | R3-3 | Context | §9 "Skills" | B01, D9 | context cases | open |
| B09 | R3-3 | Setup | §9 "Skills" | B01, D9 | `setup-*` | open |
| B10 | R3-3 | Plan | §9 "Skills" | B01 | `plan-*` | open |
| B11 | R3-3 | Build and implement | §9 "Build logs", "Multi-repo" | B01 | `build-*` | open |
| B12 | R3-3 | Review | §9 "Review-fix loop" | B01, B11 | `review-*` | open |
| B13 | R3-3 | Done | §9 "done / change.done" | — | R21 (11); `done-*`, `single-repo-done-*` | open |
| B14 | R3-3 | Fix (the light track) | §9 "Reject / reopen / … / `fix --accept`" | B01, B05 | R21 (6); `fix-*` | open |
| B15 | R3-3 | Reject, reopen and abandon | H13, R19 | — | R21 (5); reject and reopen cases | open |
| R19 | R3-4 | Restore `start`/triage; package `reject` and `reopen` | §9, H13 | — | — | moved: B05, B15 |
| R20 | R3-4 | Publish a marketplace entry, tag a release, fix the doc claims | §11 | D8 | bar item 5 | open |
| R40 | R3-4 | A real change, run by the engineer | §12 switching criterion | all of the above | bar item 4 | open |

After this round, the findings in ANALYSIS.md that have no task are exactly the ones under "Accepted differences".

## R3-0 — the gates (first, by the reviewer)

### R21 · Extend the acceptance gate

- **Owner:** the reviewer (R3-rule 1). The implementer doesn't write or edit these tests.
- **Where:** new tests in `tests/Hoshin.Tests/Acceptance/`, beside `WorkspaceLifecycleAcceptanceTests.cs`, driving the shipped `hoshin.dll` with Claude Code hook payloads, as the R0 gate does.
- **Why:** the R0 gate covers one happy change. Each test below is a path a real user takes that no test drives end to end today. Each one names the task that should turn it green.
- **Write:**
  1. **Resume on another machine** (R05's promised test, W6): T1 built in clone A and pushed; in clone B, with an empty `.git/hoshin`, `/hoshin:build` issues T2 and the change finishes.
  2. **Decline at approval** (R22, D6): at `NeedsApproval`, the engineer declines with a reason; the change goes back to a fix step, then review, then approval, then `Done(true)`.
  3. **Migrate an in-flight change** (R10): a workspace in the original's layout, mid-build with T1 logged, runs `hoshin migrate`. Hoshin.Net's hooks then finish the change to `Done(true)` without anything being re-accepted.
  4. **Start routes the change** (B05): `/hoshin:start` with a one-repository bug routes to the light track, and with a feature routes to intent.
  5. **Reject and reopen** (B15): a typed reject closes a change; a typed reopen of the spec reopens it, and re-acceptance builds on.
  6. **The light track, end to end** (B14): `/hoshin:fix`, accept, build, review, done in a single-repository workspace opens one pull request.
  7. **Multi-repository failures** (R12): a task whose repository isn't checked out fails with a message naming it, and commits nothing in the master; a `build.start` refused on a dirty repository issues no task.
  8. **A stray commit** (R11): the engineer commits by hand during attempt 1; attempt 2 starts from the task's start and can pass.
  9. **Co-located tests** (R25): one task each with `src/x.spec.mjs`, `__tests__/x.test.mjs`, `app/test_x.py` and `greet/greet_test.go` and no `testPatterns`, each accepted on its first attempt. Skip a language only when its toolchain is missing from the runner, and fail in CI if it is missing there.
  10. **Two sessions** (R23): while the master session writes the review, `/hoshin:build` in a product repository that has no tasks left leaves the master's run alone, and the review is recorded.
  11. **Every pull request is reported** (B13): `done` on a two-repository change reports both pull request URLs.
- **Done when:** the tests are committed, each fails at the point its task names, and the PR's Tasks table lists where each one fails.

B02 and B03, the eval gates, are written up in R3-3 with the other EPIC-B tasks, but they land here, before R3-1.

## R3-1 — lifecycle and integrity

### R10 · `migrate` keeps in-flight changes valid

- **Fixes:** L13, L14, L15, L16, the rest of L1
- **Where:** `Hoshin.Migration` (`DocumentMigrator`, `YamlScalarFormatter`, `YamlJsoncEmitter`), `MigrateCommand`, `WorkspaceMigrator`, and the pin checks in `ChangeState`.
- **Problem:** `migrate` rewrites documents but keeps their `source_intent_commit` and `source_spec_commit` pins, so every accepted spec and plan reads as broken afterward and has to be regenerated and re-accepted (L13). It deletes `change.yml` without reading `change.jsonc` back (L14). It writes `null` and booleans as strings in keys it always quotes (L15). It crashes partway through a run on a number that overflows, leaving some changes migrated and some not (L16).
- **Do:**
  1. Check every input first. Any refusal, including an overflowing number, stops the run before anything is written, and the report names the file and the value.
  2. Write each output atomically, read it back, parse it, and compare it with the parsed source. Delete the source only when they match. A mismatch keeps the source and is reported.
  3. Keep `null` and booleans typed in the keys that are otherwise written as strings, recursively. A pin that was `null` stays `null`.
  4. Commit the migrated documents of each change in one commit. Then re-pin every pin that held before the migration to that commit. A pin that was broken before stays broken; the migration doesn't launder it.
  5. Say in README and KATA_MIGRATION how an in-flight change moves over: finish or migrate, then continue with Hoshin.Net.
- **Test first:** an original-layout workspace with an accepted intent, spec and plan and T1 logged: after `hoshin migrate`, `hoshin state` shows every pin holding and T2 as the next task. Changes `a`, `b` and `c` where `b` holds `0xFFFFFFFFFFFFFFFFFF`: exit non-zero, no file changed in any of them, and the report names `b`. `source_intent_commit: null` stays `null`.
- **Done when:** the tests pass, and R21's migrate test (3) passes.

### R11 · One stray commit doesn't burn every attempt

- **Fixes:** W7, H11, S17
- **Where:** `TaskEvaluator.ValidatePreConditions`, `WorkflowHeadGuard`, and the test-commit/production-commit pair in `TaskVerdictPolicy` and `TaskVerdictRecorder`.
- **Problem:** the task's start HEAD stays the first attempt's. After a commit the workflow didn't make, every remaining attempt fails "HEAD moved outside the workflow", because the guard denies the agent `git reset`. When the harness kills a hook between the library's own two commits, the library's half-commit is blamed on the agent the same way. And the check compares only HEAD's commit, so after `git switch -c other` the verdict commits land on the wrong branch (S17).
- **Do:**
  1. After a "HEAD moved" verdict, the library moves the branch back to the task's start HEAD, keeping the working tree. It names the commits it moved past in the verdict and the ledger, so nothing is lost. The next attempt starts from there.
  2. Before the first of the two commits, record a pending-commit entry in the ledger (start HEAD, task, attempt). On the next hook, if the entry exists and HEAD is its test commit, finish the pair, or undo it, instead of judging the agent. Remove the entry once the pair is complete.
  3. Record the branch name with the start HEAD, and refuse to judge when the branch changed. This is the library-side check ANALYSIS §6 asks for, so the guard's missing `switch`, `branch -f` and `update-ref` rules stop mattering.
- **Test first:** a manual `git commit` during attempt 1: attempt 1 fails with "HEAD moved" naming that commit, and attempt 2 starts at the start HEAD and passes. With a committer that fails between the two commits: the next hook completes the pair, the verdict passes, and the attempt count doesn't grow. After `git switch -c other` during `Implement`: the verdict refuses and nothing is committed on `other`.
- **Done when:** both pass, and R21's stray-commit test (8) passes.

### R12 · Honor event refusals; fail loudly on an unresolved repository

- **Fixes:** W8, W9
- **Where:** `WorkflowAdvancer` (where `build.start` fires), `TaskEvaluator` (where `task.done` fires), `TaskRepoResolver`.
- **Problem:** a refused `build.start` (dirty repository, wrong branch, branch checked out elsewhere) is recorded as a failed baseline, and the task is issued anyway, so it can commit on whatever branch is checked out, `main` included. A failed `task.done` is dropped. A task whose repository isn't checked out runs in the master, and a single-repository app's task runs at the repository root instead of in the app folder.
- **Do:**
  1. A refused `build.start` issues no task. The step tells the engineer what was refused and what to fix, and the next `/hoshin:build` tries again.
  2. A failed `task.done` is reported in the verdict and to the session. Whether it fails the task follows the event's `onFailure`.
  3. `TaskRepoResolver` returns a failure when the repository isn't checked out or the app folder doesn't exist. It never falls back to the master or the repository root.
- **Test first:** a dirty product repository: `/hoshin:build` explains the refusal, issues nothing, and `main` gains no commit. A task for `web` with no `web` checkout: the message names `web` and the path where it was expected. A single-repository app's task runs in its app folder.
- **Done when:** the tests pass, and R21's multi-repository test (7) passes.

### R13 · Pin the base ref; authenticate approvals

- **Fixes:** S12, S13, and the rest of S1 and S6
- **Where:** `BaseBranchChecksReader`, `WorkflowPlanLoader`, the `change.jsonc` writer in `ChangeStarter`, the approval path in `StartHandler` and `IApprovalRecorder`, `Publisher` and `ChangeDoneStep`.
- **Problem:** checks and hook config are read from the local base branch, which the agent may move during `Implement` (`git branch -f main HEAD~5`) without moving HEAD (S13). Approvals, reviews and verdicts sit in an agent-writable JSON file; one forged `workflowApprovals` entry leads to `Publish` on the next Stop (S12).
- **Do:**
  1. `change start` records the base commit's SHA in `change.jsonc`. Checks and hook config are read from that SHA, never from a branch name.
  2. An approval is written only from a typed user prompt event, as a commit in the master with `Approved-Head: <code head>` and the git user's identity in trailers, as well as the ledger record. `Publish` requires that commit for the current code head. A ledger approval without it is ignored and reported in the trace.
  3. Say in DESIGN.md what this does and doesn't protect against. It raises the bar from editing one JSON file to forging a commit through a guard escape, which is the original's level or better. That is the goal, not tamper-proofing.
- **Test first:** `git branch -f main HEAD~1` during `Implement`: checks still come from the pinned SHA. A forged `workflowApprovals` entry: `Next` stays at `NeedsApproval`, and the trace reports the ignored record. A typed accept: an approval commit with the trailers, then `Publish`.
- **Done when:** the tests pass, and the whole-change gate still passes.

### R14 · Event hooks and `red record` get the allow-listed environment

- **Fixes:** S9
- **Where:** `HookProcessRunner` (`Events/HookProcess.cs`), `RedCommand` and `RedRecorder`, and the allow-list the test runner uses. Find its owner and reuse it (CONTRIBUTING R1).
- **Problem:** tests and git run with secrets stripped, but event hooks and `red record` get the full environment, so the "secret-free execution" claim is false.
- **Do:** run both with the same allow-listed environment as tests, plus the push and pull-request variables the publish path already keeps. A team hook that needs more names the variables in its hook config (`passEnv`); add the field to GRAMMAR.md. The original passes hooks its whole environment, so without `passEnv` a team's existing hook that reads a token would break on upgrade.
- **Test first:** with `AWS_SECRET_ACCESS_KEY` set in the hoshin process, an event hook that prints its environment doesn't see it, and neither does `red record`. A hook with `passEnv: ["JIRA_TOKEN"]` sees that variable and no other secret.
- **Done when:** the tests pass, and ANALYSIS §11.1's "secret-free execution" row is true.

### R15 · Retry atomic replace on Windows sharing violations

- **Fixes:** L17
- **Where:** `Common/FileSystem.cs` (`WriteAllTextAtomic` and the reads), `LedgerStore`, `WorkflowRepo.ReadRunMarker` and `ReadStep`, `TurnState`.
- **Problem:** on Windows, replacing a file fails while anything reads it without the lock. With 2 unlocked readers and 2 writers, about 99% of writes failed. Hooks read the ledger, `run.json` and `step.json` without the lock on every tool call, and parallel tool calls fire hooks concurrently.
- **Do:** one helper that every reader and writer of those files uses. Writes retry the replace on a sharing violation or access denial with a short bounded backoff (for example, up to 2 seconds in total). Reads open with `FileShare.ReadWrite | FileShare.Delete` and retry on the same errors. Past the bound, the error surfaces through R01's reporting.
- **Test first:** 2 readers and 2 writers on one file for 2,000 iterations: no write fails and every read parses. It runs on both operating systems; on Linux it guards against regressions.
- **Done when:** the test passes in CI on Windows.

### R16 · Narrow the guard; cover MCP tools that run commands

- **Fixes:** S18, and the gap R09 left
- **Where:** `ShellWriteTargets`, `GuardTamperingProtection`, `GuardCommand`, `HarnessTools.ClaudeMcpWritePushMatcher`.
- **Problem:** the guard denies ordinary work: redirects to logs and scratch files, quoted `>` characters, any path containing "hoshin", and `git commit -m "fix -n"` (S18). Meanwhile R09's MCP matcher lists write and push verbs but not the verbs of MCP tools that run commands (`execute_command`, `start_process`, `run_terminal`), so a shell reached through an MCP server can `git push` unseen, which is S11 again through another tool.
- **Do:**
  1. Parse redirects with quoting in mind. Allow redirects to files outside the repository and to ordinary files in it. Match protected paths by their resolved path, never by the substring "hoshin".
  2. Add the command-running verbs (`exec`, `execute`, `run`, `process`, `shell`, `terminal`, `command`) to the MCP matcher, from the same single definition R09 made.
  3. Don't chase the remaining escapes of S3, S4, S5 and S7: they are off the bar ("Accepted differences"), and R13 moves the checks that matter into the library.
- **Test first:** a theory where every S18 example in ANALYSIS §6 must be allowed, and another where every S11 example plus `mcp__desktop__execute_command` and `mcp__shell__run` carrying `git push` must be denied during a build.
- **Done when:** both theories pass, and the shipped `hooks.json` matcher still comes from `HarnessTools`.

### R22 · The engineer can decline at approval

- **Fixes:** W3 (the approval part R06 left), DESIGN.md:114 (ANALYSIS §11.2)
- **Needs:** D6 (answered: `/hoshin:decline <reason>`)
- **Where:** `StartHandler` (the approval path), `SkillInvocationDetector`, `skillMeta` in `packaging/package.ps1`, `IApprovalRecorder`, `WorkflowEngine.NextPostBuild`, `PostBuildFixEngine`.
- **Do:** package `decline` as a typed-only skill (`disable-model-invocation`). A typed `/hoshin:decline <reason>` at `NeedsApproval` records a declined approval with the reason at the current code head. `Next` then returns the same fix step as a review finding (R06), carrying the reason. A decline without a reason is refused with a message asking for one. A decline anywhere but `NeedsApproval` changes nothing and says so. A model-sourced decline is refused, as accept already is.
- **Test first:** at `NeedsApproval`, a typed decline with a reason: the next step is a fix step that carries the reason, then checks, review and `NeedsApproval` again. A decline with no reason, a decline at `Review`, and a model-sourced decline each change nothing.
- **Done when:** the test passes, R21's decline test (2) passes, and DESIGN.md's state diagram matches.

### R23 · One session's hand-off doesn't end another session's run

- **Fixes:** S16, and a defect R03 and R07 added together
- **Where:** `StartHandler.Handle` (the `if (step is Done)` branch that deletes `run.json`) and `HandleReject`, `WorkflowEngine.SelectNextTaskId` and `NextPostBuild` (the per-session `Done(true)` messages), `StartHandler.IsBlockedByOtherActiveBuild`.
- **Problem:** R03 made a product-repository session end with `Done(true)` when its own tasks are finished ("next step: run `build` in …" or "run `review` from the master"). R07 made `StartHandler` delete the workspace's shared `run.json` on any `Done`. `IsBlockedByOtherActiveBuild` blocks other sessions only during `Implement`, `Retry` and `NeedsApproval`. So while the master session writes the review, `/hoshin:build` typed in a product repository with nothing left deletes the run. The master's Stop then finds no run, stays silent, and the review is never recorded.
- **Do:**
  1. Give "this session's part is finished" its own step kind (for example `Handoff`), distinct from `Done`, so only a change-level `Done` clears the run.
  2. Clear `run.json` only from the session that owns the run, or when the change itself is done.
  3. Extend `IsBlockedByOtherActiveBuild` to every step that has an owner, so another session can't take over a document, review or fix step (S16). The blocked session is told who owns the run; it doesn't get silence.
- **Test first:** the master session at the review step, then `/hoshin:build` in a product repository with no tasks left: `run.json` still exists, the product session is told the review is next, and the master's review is committed and recorded.
- **Reviewer correction (2026-11-26, while writing R21):** the path in **Problem** does not happen as written. A typed `/hoshin:build` in a product repository with nothing left is refused by `WorkflowEngine.Gate` ("`build` cannot run on this change now") before `StartHandler` can delete `run.json`. The defect R23 fixes is still real through item 3 (S16): a second session that types the owned step, here a second master session typing `/hoshin:review` while the first writes the review, gets the same brief and takes the run over. R21 (10) drives that path, and keeps the product-session build as its first half. Items 1 and 2 still stand: nothing may clear another session's run.
- **Done when:** the test passes, and R21's two-session test (10) passes.

### R24 · Commit only what the task changed, and check Green on a clean HEAD

- **Fixes:** E16, E17, E21, E12 (deleted tests)
- **Where:** `TaskVerdictPolicy` (Green), `WorkingTreeSnapshot`, `TaskEvaluator` (`PreIssuePaths`), `DocumentCommitter.CommitReview`, the Red worktree from R04.
- **Problem:** Green runs in the dirty working tree, so a task can pass by importing a file it didn't commit (E16). A file the engineer had dirty before the task, which the task then also edits, is committed whole, the engineer's changes included (E17). `PreIssuePaths` is frozen at attempt 1, so files created between attempts are committed. A standalone `CommitReview` doesn't check that a new review was appended, so touching `review.md` re-records the old review at the new code head. A deleted test file counts as a written test. The engine's `reds` ledger entries are made up (exit code 1, the summary as the output), and `red show` reports them as real. A task that only adds a protecting test, one that passes before the change and guards existing behavior, can't pass, although the original allows it (its `fix-accepts-a-task-with-a-protecting-test` case).
- **Do:**
  1. Run Green a second time in the detached worktree at the new HEAD (R04's worktree and links). A task passes only when it passes there.
  2. Refuse to issue a task while tracked files are dirty, in a standalone repository too, as `build.start` does in a workspace. Tell the engineer to commit or stash. Don't try to split hunks.
  3. Take `PreIssuePaths` again at each attempt's issue.
  4. `CommitReview` requires a new `## Review <n>` section.
  5. A deleted test file doesn't count as a written test.
  6. `reds` entries record the Red run's real exit code and the tail of its output.
  7. A task whose test passes before the change is accepted as a protecting test when the plan marks it as one, following the original's plan rules. Anywhere else it is still invalid Red.
- **Test first:** the cases from ANALYSIS §7 and §10.4 (6): an import of an untracked helper fails Green; a dirty tracked file makes the task refuse to issue; a file created between attempts isn't committed; a touched `review.md` without a new section records nothing. Also: `red show` prints the real Red output, and a marked protecting-test task passes while an unmarked one doesn't.
- **Done when:** the tests pass.

### R25 · Find co-located tests by default

- **Fixes:** E18
- **Where:** `WorkflowContracts` (`DefaultTestPatterns`), the test-file extraction from test commands, GRAMMAR.md.
- **Problem:** the default test patterns miss `greet/greet_test.go`, `app/test_greet.py`, `src/x.spec.ts` and `__tests__/`. `go test ./greet -run X` and pytest node IDs extract no test file. Those tasks fail "no test file" every time unless `testPatterns` is set.
- **Do:** add the common co-located forms to the defaults: `*_test.go`, `test_*.py`, `*_test.py`, `*.spec.*`, `*.test.*`, `__tests__/**`. Extract test files from `go test <pkg> -run` and from pytest node IDs. Keep `testPatterns` as the override, and document the defaults in GRAMMAR.md.
- **Test first:** a theory over those paths classifying each as a test; a task for each form with no `testPatterns`, accepted on its first attempt.
- **Done when:** the tests pass, and R21's co-located test (9) passes.

### R26 · The Red classifier stops misfiring

- **Fixes:** E19, the rest of E4
- **Where:** `RedClassifier`.
- **Problem:** `\berror:\s*expected\b` rejects an ordinary JavaScript assertion (`Error: expected 3 got 2`) as a syntax error. Rust's `E0432`, the usual error for a new integration test, counts as a syntax error. Exits 137 and 139 and an access violation count as valid Red. A missing pytest dependency counts as valid Red.
- **Do:** fix each misfire with a case in the classifier's table of examples. Crashes and kills (137, 139, access violation) are not valid Red. Rust's unresolved import counts as a missing symbol. A missing dependency is `CouldNotRun`.
- **Test first:** a theory with each ANALYSIS §7 E19 example and its expected kind.
- **Done when:** the theory passes, and no existing classifier case changes kind.

### R27 · Keep the engineer's git identity and signing

- **Fixes:** E20, the rest of E7
- **Where:** the git environment builder in `GitClient` (`ProcessGitClient`).
- **Problem:** the git environment drops `XDG_CONFIG_HOME`, and drops `SSH_AUTH_SOCK` on every command except push. An engineer whose identity or signing config lives in XDG config, or who signs commits through an SSH agent, gets every library commit refused.
- **Do:** keep `XDG_CONFIG_HOME`, `GIT_CONFIG_GLOBAL`, `SSH_AUTH_SOCK`, `GPG_TTY` and `GNUPGHOME` for every git command. They locate config and signing, not secrets.
- **Test first:** with `user.signingkey`, `gpg.format=ssh` and `commit.gpgsign=true` in an XDG config, and an SSH agent holding the key, a task commit is signed. Skip only where `ssh-agent` is missing, and fail in CI if it is missing there.
- **Done when:** the test passes on Linux and Windows.

### R28 · One judge per turn; refuse conflicting launches

- **Fixes:** W10, H4
- **Where:** `WorkflowAdvancer`, `TurnState`, `run.lock`, `StartChangeResolver`, the Claude Code `PreToolUse` manifest (an `Agent` matcher), DESIGN.md:210.
- **Problem:** a Stop and a SubagentStop that arrive together both run `Advance`, so `build.finish` can run twice. `run.lock` can be held for about 20 minutes and blocks the turn-state writes of hooks that time out after 120 seconds. A named new change (`/hoshin:intent <new>`) is ignored while another run is pinned. Nothing refuses a second subagent launched while one is running, although DESIGN.md says the hook does.
- **Do:**
  1. Judge each dispatch once: the first hook to take the dispatch nonce judges, and the others see it taken and stay quiet.
  2. Hold `run.lock` only around state reads and writes, never across a test run.
  3. A named new change while another run is pinned is refused with a message, not ignored.
  4. Add an `Agent` matcher to `PreToolUse` that denies a second launch during an `Implement` or fix step, from the `HarnessTools` definition.
- **Test first:** a Stop and a SubagentStop fired together for one dispatch: `build.finish` runs once. A second `Agent` launch during `Implement` is denied.
- **Done when:** the tests pass, and DESIGN.md:210 is true.

### R29 · Fail closed on bad input

- **Fixes:** S10, and the two catches C3 found hiding defects
- **Where:** `GuardCommand`, `HookCommand` (payload parsing), `ChangeFolderResolver` (the catch that swallows a corrupt `run.json`), `DocumentCommitter` (the catch that skips the spec/plan cross-check on an unreadable spec).
- **Problem:** a malformed payload, a numeric `tool_response.status` or a corrupt `run.json` makes the guard throw and allow the call. The corrupt `run.json` is also swallowed when resolving the change folder, and an unreadable spec silently skips the spec/plan cross-check.
- **Do:** during an active run, the guard denies on input it can't read, with a message that names the problem. Outside a run it stays silent (D2). A corrupt `run.json` is reported through R01's path. An unreadable spec fails the cross-check instead of skipping it. Remove both catches; don't reshape them.
- **Test first:** each of the three bad inputs during a run gives a deny; the corrupt `run.json` produces R01's message; an unreadable spec fails the plan's acceptance.
- **Done when:** the tests pass, and R17's catch scanner finds neither catch.

### R30 · Readers agree with the migrator

- **Fixes:** L8, the rest of L12, L9
- **Where:** `SpecReader`, the `change.jsonc` reader, and the hook-script export (`ls-tree` and its encoding).
- **Problem:** `SpecReader` takes the first `gherkin` fence anywhere, not the first top-level one, so an example in a list item or a blockquote becomes the spec, and its early-close scan cuts a block at an indented backtick docstring, so the reader and the migrator disagree again (L8). A schema-invalid but parseable `change.jsonc` silently falls back to `change/<folder>` (L12). The hook-script export turns Latin-1 bytes into U+FFFD, writes symlinks as text, and runs `ls-tree` without `-z` (L9).
- **Do:** take the first top-level fence and close it the way the migrator does. Refuse a schema-invalid `change.jsonc` with a message naming the field. Export hook scripts byte for byte, keep symlinks as symlinks, and use `ls-tree -z`.
- **Test first:** a spec with an example fence in a list item before the real one; a fence holding an indented backtick docstring; `change.jsonc` with `"branch": 3`; a hook script with Latin-1 bytes and a symlinked script.
- **Done when:** the tests pass.

## R3-2 — enforcement

### R17 · Make the baseline shrink-only, count every suppression, close the scanner holes

- **Fixes:** C1, C2, C3, C4
- **Where:** `tests/Hoshin.Tests/Architecture/ArchitectureTests.cs` (`CompareBaseline` and the rule scanners), `baseline.json`, `.editorconfig`, `Directory.Build.props`, `.github/workflows/ci.yml`.
- **Problem:** see ANALYSIS §10.1. In short: a PR can add a violation with its baseline entry; `.editorconfig` severities, `<NoWarn>`, `GlobalSuppressions.cs` and most `SuppressMessage` forms go uncounted; and the scanners miss `catch (…) when (…)`, clock reads inside interpolated strings, `?? new`, inherited interface members and mutable `static readonly` arrays, and never scan `Hoshin.Migration`.
- **Do:**
  1. A CI step compares `baseline.json` with `origin/main`'s and fails when any entry was added. There is no override label. A switch that turns a rule off will get used, and a rule that has been switched off can't report it. A rule that keeps needing exceptions should be removed.
  2. Count every suppression channel: `.editorconfig` severities for `CA*` and `IDE*`, `<NoWarn>`, `GlobalSuppressions.cs`, every `SuppressMessage` form, and `#pragma warning disable`. Each one needs a baseline entry, or the test fails.
  3. Close each scanner hole ANALYSIS §10.1 lists, and scan `Hoshin.Migration`.
  4. Fix what the new scans find rather than baselining it. The two catches that evaded the old scanner are R29's.
  5. Guard the guards: each scanner fails when it scanned zero files or types, and the CI gate fails when the architecture tests report fewer cases than `main` does.
  6. C4: add a per-file floor to the changed-line coverage gate, run `dotnet format --verify-no-changes`, and keep the coverage variables in the black-box suites that run with `bareEnv`.
- **Test first:** for each new scan, a seeded violation (a source fixture) that the scan must report, and an empty input it must refuse.
- **Done when:** the tests pass, CI fails a PR that grows the baseline, and CONTRIBUTING's "CI-checked" claims are true.

### R18 · Run the `yaml.mjs` differential in CI; record real harness payloads

- **Fixes:** ANALYSIS §8 (the differential), §10.4 (8), T38's leftover
- **Where:** `YamlDifferentialTests`, `.github/workflows/ci.yml`, `tests/Hoshin.Tests/Fixtures/hooks/`, `HarnessPayloadFixtureTests`.
- **Problem:** CI never checks out `hoshin`, so the comparison with `yaml.mjs` is skipped silently, and it never compares refusals. The hook fixtures are hand-written (`/workspace/repo`, a `deadbeef` nonce), so the payload shapes the async flow relies on are unverified.
- **Do:**
  1. CI checks out `hoshin` at a pinned commit beside this repository, with Node, so the differential runs. When `CI` is set and the sibling is missing, the test fails instead of skipping.
  2. Compare refusals: both parsers refuse, or the test fails. A refusal is the migrator's own error type; any other exception fails.
  3. Add a recorder (a hook command that writes its stdin, anonymized, to a fixture folder). The engineer records one real Claude Code session on Windows and one on Linux that touch every event and matcher in `hooks.json`, `PowerShell` and an MCP tool included, and one Antigravity session. Replace the hand-written fixtures with the recordings.
  4. Close the black-box gap (ANALYSIS §9, "Black-box tests"): the original has 52 suites and Hoshin.Net has 44. For each missing one (`apps`, `check-events`, `check-read`, `coverage-ignore`, `diff-coverage`, `events-single-repo`, `single-repo-marker`, `system-read`), port it, or name the C# test that covers the same cases, in a table in the black-box README.
- **Test first:** the differential fails in CI with the sibling missing; `HarnessPayloadFixtureTests` fails when an event or matcher in either manifest has no recorded fixture.
- **Done when:** the differential runs in CI and compares refusals, every manifest event and matcher has a recorded fixture, and every missing black-box suite is ported or mapped.

## R3-3 — parity: the judgment layer (EPIC-B)

EPIC-B was "open by design" in rounds 1 and 2. These tasks replace it. They come from a line-by-line comparison of the original's skills, references, templates and evals with Hoshin.Net's procedures, briefs and hooks. Cites into the original (`hoshin/…`) are at `hoshin@47f9d7b`.

That comparison changed the shape of the work. About half of what the skills lack is not text: the engine contradicts the original (intent's scope), gives the model no data (the review brief), or has no mechanism at all (setup, context, start). So each skill task has two parts:
- **Engine:** behavior in the library or hooks, tested in C# first, like any round-3 task.
- **Judgment:** rules for `procedure.md`, its `references/`, or the brief. They are written harness-neutrally (AGENTS.md invariant 5) and judged by the task's eval cases.

Text that drives `hoshin.ts` in the original is not ported; the engine does that work (R3-rule 3). A task's eval cases are named in B03's table.

### B01 · Templates and shared references

- **Where:** `skills/*/references/`, `src/Hoshin.Workflow/Workspace/Templates/`, `WorkflowBriefs`, `docs/GRAMMAR.md`; the originals are `hoshin/references/templates/*`, `hoshin/references/triage.md` and `hoshin/references/written-rules.md`.
- **Problem:** Hoshin.Net has no intent, spec, plan, context or policies template. The briefs carry format skeletons only. Rules that several skills share in the original (triage's boundary rules, especially rule 3's definition of a new behavior; how to read and apply a repository's written rules) exist nowhere. The workspace templates still name `start` and are otherwise stale. Per-system template overrides (`hoshin/references/workspace.md:79-85`) have no lookup.
- **Do:**
  1. Port the intent, spec, plan, context and policies templates to JSONC layout 2: line-1 ```` ```jsonc ```` front matter, task blocks as ```` ```jsonc ```` objects with the `PlanReader` keys, and Shared decisions as a ```` ```jsonc ```` array. Leave out the fields the engine writes (`source_*_commit`). Each template passes its document's check once its placeholders are filled, and fails it while they aren't.
  2. Port `triage.md` and `written-rules.md` as shared references, harness-neutral, and package them for every skill that cites them (start, fix, review, plan).
  3. Give the briefs the template path instead of an inline skeleton, so the rule has one owner (CONTRIBUTING R1).
  4. Look up a per-system template override in the master before the packaged template, as the original does.
  5. Update the workspace templates for round 3's behavior, and fix GRAMMAR.md's plan section, which still says `yaml`.
- **Test first:** each template with placeholders fails its check and, filled from a fixture, passes; a system override is used over the packaged template; the strict skill lint passes over every new references file.
- **Done when:** the tests pass, and `ignores-system-plan-template` passes.

### B02 · An eval runner for the Claude Code bundle

- **Owner:** the reviewer (R3-rule 1). It lands in R3-0, before R3-1.
- **Where:** a new `evals/` folder (`README.md`, `run-wsl.sh`, the runner config), `packaging/package.ps1`, CI.
- **Problem:** Hoshin.Net has no evals. The original runs `claude plugin eval` with a sandbox that needs Linux or WSL, `TMPDIR` outside `/tmp`, and no `Agent` tool (`hoshin/evals/README.md`). Pointing it at Hoshin.Net's bundle has its own traps: every hook calls bare `dotnet`, and without it the hooks fail without blocking, so a case can pass `skill-fired` while nothing runs. `EventRunner` and `HookScripts` create folders at the `$TMPDIR` root, which the sandbox may deny. Typed-only commands (accept, done, reject, reopen, `fix --accept`) are refused when the model invokes them, which is how a plain-text prompt reaches them.
- **Do:**
  1. A runner that builds the bundle into a folder `package.ps1` doesn't wipe, points every case's `plugins:` at it, and refuses to start without a .NET 10 runtime inside the sandbox.
  2. Every case fails if `hooks.jsonl` has no entry for the session, so a silently dead hook can't pass.
  3. Find out whether a case prompt that starts with a slash command reaches the hooks as typed by the user. Record the answer in `evals/README.md`. If it doesn't, the typed half of each case is covered by B03's C# tests, and the eval grades up to the typed step.
  4. A CI workflow that runs the suite on a label and on release tags, on Linux, with a cost cap, and writes the run under `evals/results/<timestamp>/`. Not on every push: a full run costs about $0.50 per case per run.
  5. Find out whether the runner accepts JSON case files. If it doesn't, add `evals/**/case.yaml` to AGENTS.md invariant 4 as its one more exception, with the reason.
- **Done when:** one trivial case passes through the runner on Linux and in WSL, and a case with the runtime removed fails at startup instead of passing.

### B03 · Port the eval suite

- **Owner:** the reviewer (R3-rule 1). It lands in R3-0, after B02.
- **Where:** `evals/<case>/` for LLM cases; `tests/Hoshin.Tests/Acceptance/Parity/` for deterministic ones; `hoshin/evals/` (56 cases; an earlier count said 60) as the source.
- **Problem:** the original has 56 cases plus grader self-tests. Their graders read `hoshin.ts … commit` calls and YAML front matter, so none run as they are. About half of them test mechanism, not judgment, and an LLM run is the most expensive and least reliable way to check a mechanism.
- **Do:**
  1. Sort every original case into one of three destinations, in a table in `evals/README.md`: an LLM case (it judges the model's work), a C# parity test (it checks what the engine does), or dropped, with the accepted difference that explains it. Every one of the 56 has a row.
  2. LLM cases: rewrite the graders for what Hoshin.Net produces. Commit subjects come from the reflog (Hoshin.Net's are `docs(<scope>): draft <doc> <change>`, `docs(<scope>): log T1 <change>`, `docs(<scope>): review <change>`). Documents are read as JSONC front matter. The hook trace comes from `.git/hoshin/hooks.jsonl`. "Committed through the program" becomes "the library made the commit, and the guard denied any manual commit".
  3. C# parity tests drive hook payloads, the way the acceptance gate does, including typed-user payloads for the human-only steps.
  4. Port the grader self-tests, in the form the black-box suites use.
  5. Add cases for what the original's suite never covered but Hoshin.Net's skills now need: the per-repository review dispatch (B12) and the start-to-fix handoff (B05).
- **Done when:** every one of the 56 has a row; every LLM case and parity test fails today for the reason its row gives; and the grader self-tests pass.

### B04 · Skill descriptions that trigger

- **Where:** `skillMeta` in `packaging/package.ps1`.
- **Problem:** the generated wrappers' descriptions were shortened and lost the original's trigger phrases, so a request like "refresh the workspace instructions" may never fire setup.
- **Do:** restore each skill's trigger phrases from the original's front matter, adapted to Hoshin.Net's commands, and keep `disable-model-invocation` on the typed-only skills.
- **Test first:** a test that each generated `SKILL.md` description contains the phrases its source lists.
- **Done when:** the test passes, and every LLM case's `skill-fired` grader passes in B02's run.

### B05 · Start and triage

- **Fixes:** ANALYSIS §9 "Start / triage", the start half of R19
- **Engine:** add `start` to `skillMeta`, `SkillInvocationDetector` and the hook path. A verdict of "light track" or "full pipeline" hands off in the same session to fix or intent, carrying the verdict, without starting a second change.
- **Judgment** (`hoshin/skills/start/SKILL.md`, `hoshin/references/triage.md`): classify the kind (bug, enhancement, feature) and ask the one question that settles it; boundary rule 2 in full (settling an `intent unknown` rule, removing an oddity that isn't a bug, or narrowing a capability all fail it, and nothing said in the session changes a tag); rule 3's definition of a new behavior; never split one change into several fixes; a missing context file means "can't route yet"; route a plainly large change early from its description; a bug needs its root cause at `file:line` and a reproduction test before it can be routed; the verdict layout and its fixed choice sets.
- **Test first:** a hook test that `/hoshin:start` followed by a light-track verdict hands off to fix in the same session, with one change.
- **Done when:** the test passes, R21's start test (4) passes, and the start cases pass.

### B06 · Intent

- **Engine:**
  1. Scope is kept as the engineer typed it, separate from the system key; `IntentReader` accepts any scope. A `scope:` argument is parsed, not slugged into the name.
  2. The change is started after the model proposes a name and title, not from the raw arguments before any question, and a workspace with several systems asks which one instead of failing.
  3. The draft may be written section by section: Stop commits a valid draft without advancing while it still has `undecided:` lines under Open questions, and `IntentReader` refuses a finished intent that still has one.
  4. The brief carries the `context:` field, and the session is warned when the workspace instructions or a context file it reads is stale. `StaleChecker` exists and is unused.
  5. Finding an existing change matches by title and content, not only by slug.
- **Judgment** (`hoshin/skills/intent/SKILL.md`, `references/interview-guide.md`, `existing-change.md`, `scope.md`): the seven-question cap, the legal-exposure exception, and the one batch of "engineer's call" items, in place of "four to six questions"; settle the happy path before any edge case; the two opposite failures the interview exists to prevent; anchor questions in concrete moments and never invent facts; how to treat each context tag (an `intent unknown` rule first, `seen in code` as keep or change, never present an oddity as behavior to keep, skip the bug or obsolete ones, never quote the Note column); the stop rule and its closing line; read the document back, keep it to 1-2 pages, scan it for technology; the interview guide (the solution-to-need table, the 12 edge-case categories, safe defaults, how to break analysis paralysis) as `references/interview-guide.md`; resuming an existing change.
- **Test first:** hook tests for engine items 1-3: `PROJ-123` kept as typed; two systems give a question, not a failure; a valid draft with an `undecided:` line is committed without advancing.
- **Done when:** the tests pass and the intent cases pass.

### B07 · Spec

- **Engine:** the spec brief names the context files and the system's `policies.md`, and lists the policy areas from it; nothing in Hoshin.Net reads `policies.md` today. `doc.accept` fires on acceptance (ANALYSIS §9, "Lifecycle events"). The session is warned of stale context, as in B06.
- **Judgment** (`hoshin/skills/spec/SKILL.md`): the engineer's call is decided in the spec and recorded there, which reverses `procedure.md:13`; invoke each named policy skill, fill `policies_applied` and `policies_not_checked`, say why an area wasn't checked, never infer a policy skill, and treat a missing `policies.md` as every area `none`; the Design section, the constraint-to-requirement table, the decisions table, and Areas of concern with owners, including any `intent unknown` rule the design relies on; Gherkin rules (`Feature:` is the intent's title plus its outcome, `@from:` tags without spaces, IDs stable on regeneration, one Scenario Outline listing every reachable combination once there are three or more conditions); read the code to make the design concrete; stop and commit nothing when the intent is too thin to design from.
- **Test first:** a hook test that the spec brief lists `policies.md`'s areas, and one that accepting a spec fires `doc.accept`.
- **Done when:** the tests pass and the spec cases pass.

### B08 · Context

- **Needs:** D9
- **Engine** (D9, answered (a)): today `hook start` returns empty output for context, writes no run, and the strict lint forbids the procedure from calling git, so `source_commit`, the diff since it, the rule-number history and the `context` commit are all out of reach. So `/hoshin:context` gets a hook-issued step: the brief carries each repository's current commit, its `source_commit` and the paths changed since, and Stop checks and commits the files it wrote, under the `context` commit kind. It still never touches another change's run (H2).
- **Judgment** (`hoshin/skills/context/SKILL.md`): where each answer lands (a bug or obsolete behavior becomes a Known oddity tagged `confirmed by engineer`; "I know what but not why" becomes `intent unknown`, noting who might know); refreshing (carry unchanged rules forward, retire and renumber a changed rule, a regression becomes an oddity, "can't say" becomes `intent unknown`, and report the counts carried, changed, added and removed); group questions by repository and keep their number in proportion; a repository skipped as current still joins the seam pass; ignore the master's `intent/` and `.hoshin.json`; a single-repository workspace uses the app as its unit; the front matter fields.
- **Test first:** a hook test that `/hoshin:context` in a workspace gives a brief carrying each repository's `source_commit` and changed paths, and that Stop commits a valid context file under the `context` kind.
- **Done when:** the test passes and the context cases pass.

### B09 · Setup

- **Needs:** D9
- **Engine** (D9, answered (a)): the CLI has all of setup's mechanism (instructions, naming, hooks, check, the `setup` and `instructions` commit kinds), but the skill can't reach it on the hook path. So `/hoshin:setup` gets a hook-issued step in each of its modes, and the library writes and commits what the engineer settled.
- **Judgment** (`hoshin/skills/setup/SKILL.md`, `references/new-master.md`, `hooks.md`, `naming.md`): the instructions-only, naming-only and hooks-only modes; creating a new master, and the single-repository offer; for an existing system, ask only about adding or removing repositories, and keep them when the engineer is away; a repository may belong to several systems; refuse a hook on a deprecated event and name the three events that keep hooks; hooks and naming apply only once committed on the base branch; ask before writing a `CLAUDE.md` in the home folder; never hand-edit the generated instructions; commit only files this run wrote; the report and the next step.
- **Test first:** hook tests: setup in instructions mode replaces the marked block and keeps the text around it; a hook on `task.done` is refused and nothing is written.
- **Done when:** the tests pass and the setup cases pass.

### B10 · Plan

- **Engine:** the plan brief lists the `build.finish` hooks on the base branch (`BaseBranchChecksReader`), names every task field (`compile`, `uses`, `files` and `supports` are missing), and carries the engineer's direction from the command's arguments. Today the arguments appear only in the prompt. Declining to write (direction that contradicts the spec, or an outline row the code can't meet) is a clean outcome: Stop lets the session end without a plan and says "reopen the spec".
- **Judgment** (`hoshin/skills/plan/SKILL.md`, `references/drafting.md`, the shared written-rules reference): `test` and `compile` are argument lists whose first item runs without a shell, so `npm`, `npx` and `.bin` shims are out (`node <script>`, `python -m pytest`), and paths are relative to the repository, with no `cd`; the filter matches exactly one test by its full name; `compile` builds only the test project, and stubs come with the test; decide whole-application checks by scope, name a missing check under `## Checks at build finish`, and tell the engineer to add it as a hook; record direction under `## Engineer direction` as "point → where it landed", or "None given"; one round of outline corrections; the decision-table rules; shared decisions; which task covers a scenario; the ten questions to interrogate the draft with; written rules (take commands from them, carry what they require, never restate a style rule, ask on a conflict).
- **Test first:** a hook test that the plan brief lists the base branch's `build.finish` hooks and the command's direction text; and one that a plan turn that declines to write ends without being pushed back.
- **Done when:** the tests pass and the plan cases pass.

### B11 · Build and implement

- **Engine:**
  1. The implement brief carries the Gherkin of the scenarios the task covers and supports.
  2. Departures and notes, end to end: the brief asks for them, the result carries them, `BuildLogWriter` writes them, and the review brief (B12) reads them.
  3. Only text after an explicit marker becomes fix instructions. Today every argument to `build`, a change name or `--repo` included, is stored as instructions.
  4. The dispatch directive names a model: the default tier for a task, one tier up after a second failed attempt. This belongs in the hook directive, not the procedure (invariant 5).
  5. Committed task work without a log entry is adopted: a task whose commits carry its `Plan-Task` trailer gets its log entry written, not issued again.
  6. The schedule message names the waiting repositories and what each waits on, not only the ready ones.
  7. A second clone of the master in the workspace is never taken as the master.
- **Judgment** (`hoshin/skills/build/SKILL.md`, `references/brief.md`, `instructions.md`): what counts as a departure and what as a note, and how to tell them apart; changing an existing test is a departure; run only the task's own "done when" commands, never whole-repository checks; review instructions that share a cause get one fix with one owner.
- **Test first:** hook tests for engine items 1, 3, 5 and 6: the brief holds the scenario text; `/hoshin:build shop` stores no instructions; a task committed with its trailer but not logged gets a log entry and isn't issued again; the schedule names a waiting repository and its blocker.
- **Done when:** the tests pass and the build cases pass.

### B12 · Review

- **Engine:**
  1. The review brief says to append, never to start a new file (it says "Start a new file on line 1" today). R24 makes `CommitReview` require a new section.
  2. The brief carries the data the original's reviewers get: each repository's base and range, the build-log paths, departures, `red:` lines and red outputs, earlier findings, and the commits since the last review.
  3. Review dispatches one reviewer per touched repository, plus one across repositories, each with that repository's slice of the brief, on the strongest model tier. Their findings are merged into one appended section.
  4. An uncommitted draft review is offered as is: the session asks whether to commit it or review afresh, and never discards it without a yes.
- **Judgment** (`hoshin/skills/review/SKILL.md`, `references/review-brief.md`): run the repository's checks in check mode, never with `--fix`, and count a failure only on lines the change touched; red-verdict findings (red not observed, output showing the wrong test, or a missing `red` line is a medium `spec` finding at the log's `red:` line), except Hoshin.Net's `not observed: no testable behavior`, which is correct for a task with no test; the light-track new-behavior count by triage rule 3; drop a `convention` finding whose cited file the reviewer didn't read, and never base a finding on a rule that only states a value; the report items (baseline still failing, approved and fixed; unhooked checks; tasks with no stored red; what comes next).
- **Test first:** hook tests: a two-repository review dispatches three reviewers, each brief holding its repository's range and log; a second review appends a section and keeps the first.
- **Done when:** the tests pass and the review cases pass.

### B13 · Done

- **Engine:** report every repository's pull request URL, not only the master's (`Publisher.ExtractPullRequestUrl`), and the apps the change didn't touch.
- **Judgment** (`hoshin/skills/done/SKILL.md`): what comes next: remove the change once merged; a missing review means run `review`; a check that hasn't passed means run `build` in that repository.
- **Test first:** a two-repository publish reports both URLs and the untouched app.
- **Done when:** the test passes, R21's test (11) passes, and the done cases pass.

### B14 · Fix (the light track)

- **Engine:**
  1. Recheck the light-track boundary at acceptance, not only at the draft commit: a plan that grew past three tasks, or a second repository, is refused at accept.
  2. The fix brief asks for `track: fix` in the intent, and `FixState` reports a missing one instead of throwing.
  3. Feedback at `AwaitAcceptFix` redrafts: the engineer's changes go back to the model as a redraft step, and the boundary is checked again.
  4. The engineer's own edits to the drafts are kept, not rewritten. The row-3 brief stops saying "write all three", and a stopped draft is finished without rewriting what exists.
  5. A large change refused at the boundary is abandoned cleanly, without leaving a started change behind.
- **Judgment** (`hoshin/skills/fix/SKILL.md`, `references/documents.md`, `states.md`, the shared triage reference): what rule 3 counts as a new behavior (a protecting test doesn't count; a Scenario Outline counts once); a refusal names the task and its tests, gives the intent command, and offers a split as the second option; a whole-repository check with no `build.finish` hook means the change isn't small; the intent skeleton (Root cause at `file:line`, the Decisions table, Open questions "None."); the spec rules (an Outline for three or more conditions, a neighboring scenario only when it's at risk, no extra requirements); the plan rules (one new failing test per task, no Shared decisions, a `test` on every task).
  6. **Reviewer addition (2026-11-26, found while writing R21):** in a single-repository workspace the master and the app are one repository, so the review commit lands on the change branch after the review recorded its head. `/hoshin:done` then refuses ("latest review covered app at X but branch is now at Y"), and a light-track change can never publish. The review's own commit, and any other master-document commit, must not count as a change to the reviewed code. R21 (6) fails here today.
- **Test first:** hook tests for engine items 1-6.
- **Done when:** the tests pass, R21's light-track test (6) passes, and the fix cases pass.

### B15 · Reject, reopen and abandon

- **Fixes:** H13, the rest of R19, ANALYSIS §11.2 (README:52, DESIGN:124)
- **Engine:** package `reject` and `reopen` as typed-only skills. Parse `intent --reject` and `fix --reject`. A rejection supersedes the plan and removes the change's copy, as the original's `change remove --abandon` does. Reopening an accepted document keeps the tasks already built and logged, instead of refusing because build work exists. Reopening on a broken pin stops and waits instead of regenerating.
- **Judgment** (`hoshin/references/rejecting-a-change.md`, `hoshin/skills/intent/references/existing-change.md`): when to reject, when to reopen, and when to decline at approval instead (R22); what to tell the engineer after each.
- **Test first:** hook tests: a typed reject closes the change, its commit is the branch tip, and the copy is gone; a reopened spec with T1 built keeps T1; a reopen on a broken pin waits.
- **Done when:** the tests pass, R21's test (5) passes, and the reject and reopen cases pass.

## R3-4 — release

### R19 · Restore `start`/triage; package `reject` and `reopen`

Moved: `start` is B05, and `reject` and `reopen` are B14, so each is judged by its own evals.

### R20 · Publish a marketplace entry, tag a release, fix the doc claims

- **Fixes:** ANALYSIS §11
- **Needs:** D8
- **Where:** `packaging/package.ps1`, the plugin's `plugin.json`, the marketplace (per D8), `CHANGELOG.md`, `CLIENTS.md`, `README.md`, `DESIGN.md`, `GRAMMAR.md`, `KATA_MIGRATION.md`, `CONTRIBUTING.md`, `WorkspaceMigrator`, `WorktreeDependencyLinks`, the workspace templates, CI.
- **Do:**
  1. Per D8: rename nothing. The plugin stays `hoshin`. Its entry in the original's marketplace (`hoshin/.claude-plugin/marketplace.json`, a change in that repository) points at Hoshin.Net's release bundle and names its version, which follows `VersionPrefix`. Upgrading is the migration path: README and KATA_MIGRATION say to upgrade, then run `hoshin migrate` (R10).
  2. Pick the version by CLIENTS.md's rule (a breaking change means a major version). Fold the untagged `[2.0.0]` and `[Unreleased]` into that release in CHANGELOG.
  3. A CI job builds the bundles with `package.ps1` on a tag and attaches them to a GitHub release. Tag a release candidate (`-rc.1`) for R40 first, and the final release from `main` after the merge.
  4. Make every row of ANALYSIS §11.1 marked false or partly, and every row of §11.2, true, or remove the claim. GRAMMAR.md's body is rewritten for JSONC layout 2 and the hook engine; no `change.yml`, `node hoshin.ts` or YAML front matter remains. `migrate` names the right plugin. The `WorktreeDependencyLinks` comment stops saying the main checkout's dependency folders are "never mutated": they are live links, so a tool writing into `node_modules/.cache` writes into the main checkout. README's Known Limitations lists the accepted differences above.
- **Test first:** a test that `plugin.json`, the marketplace entry and `VersionPrefix` agree; a doc test that GRAMMAR.md mentions none of `change.yml`, `node hoshin.ts` or `---` front matter.
- **Done when:** the release candidate is installable from the marketplace on a clean machine, and bar item 5 holds.

### R40 · A real change, run by the engineer

- **Owner:** the engineer. An agent doesn't run this task.
- **Needs:** every other round-3 task `done` or `invalid`, and the release candidate from R20.
- **Do:** install the release candidate from the marketplace in a real workspace that has a master and at least one product repository. The engineer's own `ai-sdlc` workspace qualifies. Take one real change, not a fixture, from intent to merged pull requests, using only the plugin's commands. Write `docs/review/DOGFOOD.md`: the change, every point where the engineer had to step outside the workflow, and every defect. Each defect gets a gate test and an `R-new-<n>` task (R3-rule 6).
- **Done when:** the change's pull requests are merged, and no defect from the run is open.
