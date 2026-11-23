# Hoshin.Net — Review Tasks (agent-actionable)

This file turns [ANALYSIS.md](ANALYSIS.md) into discrete work items that a coding agent can pick up one at a time. Each task gives:
- the files and symbols involved;
- the problem, as a concrete failure;
- what to change;
- the test to write **first**;
- when the task is done.

The IDs in brackets (S1, E2, L4, …) point back to the matching section of ANALYSIS.md, which has the full reasoning.

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
| T36 | P1 · SOLID | One owner for duplicated rules; enums for closed sets | E13 (O1, R1) | — | open |
| T43 | P1 · SOLID | Split `WorkflowRunner`, `WorkflowRepo` and `HookOrchestrator` | §8.1 (S1, S3) | T34 | open |
| A1 | P1 | Unify: `WorkflowRunner` uses `IGitClient` | §3.2 | D1 ✔, T35 | open |
| A2 | P1 | Unify: one Red rule | §3.2 | A1, T14 | open |
| A3 | P1 | Unify: one ledger (`LedgerStore`, per change and repo) | §3.2 | A1, T07, T19 | open |
| A4 | P1 | Unify: `WorkflowState` from `ChangeStateService` | §3.2, §6 | A1 | open |
| A5 | P1 | Unify: the runner fires lifecycle events and writes build logs | §6 | A3, A4 | open |
| A6 | P1 | Unify: multi-repo builds | §6 | A5 | open |
| A7 | P1 | Unify: start a change from `hook start` | H1 | A4 | open |
| A8 | P1 | Unify: reject, reopen and light-track accept flows; delete the unused half | §6 | A5, A7 | open |
| T21 | P2 | Small reader and validator fixes (batch) | L11, L12 | — | open |
| T37 | P2 | Fix false and stale doc claims | §7 | — | open |
| T38 | P2 | Recorded harness payload fixtures | §5.2, §9 | — | open |
| EPIC-B | P1 | Restore the dropped skill guidance, templates and evals | §6 | — | open (needs a human reviewer) |

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

ANALYSIS §6 lists what was dropped. This needs a human reviewer for each skill. The judgment rules have to be rewritten harness-neutrally to satisfy AGENTS.md invariant 5. Port `hoshin/references/templates/*` and the `hoshin/evals` cases, and add an eval runner. Make this its own plan, with one PR per skill.
