# Round 3 handoff: from the reviewer's session to the implementing session

Written 2026-11-26, and last updated 2026-11-27, by the reviewer's session, which wrote round 3's task list and its gates. Read this, then `docs/review/TASKS.md` from "The replacement bar" to the end. TASKS.md is the authority; this file only says where things stand and what will trip you.

## Who you are, and the one rule you can't break

You implement every round-3 task except R21, B02, B03 and R40. You have not seen the gates being written, and that is the point (R3-rule 1).

- Never write, edit, skip, filter, rename or re-trait anything under `tests/Hoshin.Tests/Acceptance/` (that includes `Harness/`) or `evals/`. The only exception is new run records under `evals/results/`.
- Don't change CI or `evals/run.sh` so that a gate stops running.
- If a gate looks wrong, add it to the "Gate disputes" section of the pull request with the evidence, and keep working. The reviewer decides.

`.github/CODEOWNERS` on `main` (PR #7) names the reviewer as owner of the gates, `evals/` (but not `evals/results/`), `ci.yml`, `evals.yml` (removed since: the suite runs only in WSL) and itself. Branch protection doesn't require code-owner review yet, and you push under the same GitHub account as the engineer, so no GitHub setting can tell your commits from theirs. The reviewer checks `git log origin/main..HEAD -- tests/Hoshin.Tests/Acceptance evals .github` before merge, and any commit there that isn't R21, B02, B03 or a reviewer's `R-new` gate is reverted. Treat the rule as a hard wall.

## What is on the branch

`fix/round-3`, from `main` at `08ceece` (PR #6). Pushed and in sync with `origin`.

| Commit | Task | What |
| :--- | :--- | :--- |
| `82fb3d4` | R21 | The user-path acceptance gate. The R0 harness moved to `tests/Hoshin.Tests/Acceptance/Harness/`, shared by both gates; R0 still passes 3 of 3. |
| `f11fd78` | B02 | The eval runner (`evals/run.sh`, `evals/run-wsl.sh`, and a CI workflow removed later), the smoke case and the typed-command probe. |
| `5d47acd` | B02 | `.gitattributes`: shell scripts stay LF on every checkout. |
| `1498f52`, `39d2457` | — | This handoff, the index statuses, and B14 engine item 6. |
| `fa8194f` | B03 | The ported eval suite (39 cases, with grader self-tests), the C# parity tests under `Acceptance/Parity/`, and `EvalGraderSelfTests`. |
| `0fa44bb`, `0b9c87f` | B02 | From the first paid runs, and the engineer's WSL-only decision: the runner stages the bundle as the eval target, the cost cap's default is $70, the typed-command answer (yes), and the R-new-1 gate. |
| the last reviewer commit | — | This handoff's final update: the trailer check (PR #9) and the draft pull request (#10). `conventional` checks trailers on every commit after it. |

## Where the gate fails today

Last full run: 20 acceptance tests, 7 pass (R0 ×3, R21 (1) resume, R21 (9) for Node `*.spec.mjs`, Node `__tests__/` and Python). The fast suite passes 405 of 405 and the build has 0 warnings.

| R21 test class | Fails at | Task that turns it green |
| :--- | :--- | :--- |
| `R21_02_Decline` | `/hoshin:decline` is not a command; no fix step follows | R22 |
| `R21_03_Migrate` | after `migrate`, `intent.md` is left uncommitted, so build refuses | R10 |
| `R21_04_StartBug`, `R21_04_StartFeature` | `/hoshin:start` reaches no hook | B05 |
| `R21_05_Reject` | the change's copy survives the rejection | B15 |
| `R21_05_Reopen` | reopening the spec is refused once T1 is built | B15 |
| `R21_06_LightTrack` | `/hoshin:done` refuses: the review commit moved the single-repository branch | B14 (engine item 6), then B13 |
| `R21_07_MissingCheckout` | `hook start` throws `CouldNotRunException` and the crash is swallowed | R12 |
| `R21_07_DirtyRepository` | T1 is issued anyway | R12 |
| `R21_08_StrayCommit` | attempts 1, 2 and 3 all fail | R11 |
| `R21_09_CoLocatedTests(go)` | "no test file was added or modified" for `greet_test.go` | R25 |
| `R21_10_TwoSessions` | a second master session typing `/hoshin:review` gets the same review brief | R23 |
| `R21_11_Done` | only the master's pull request URL is reported | B13 |
| `WorkspaceInsideARepositoryAcceptanceTests+RNew1_WorkspaceInsideARepository` (both cases) | a session in the workspace folder, inside an enclosing git repository, is told "There is no change plan in this repository" | R-new-1 |

`RNew1_WorkspaceFolderAlone` in the same file passes today and must stay green. Each test's `<summary>` in `UserPathAcceptanceTests.cs` names its R21 item and its task. Read the test before you start its task: it is the most exact statement of "done" you have.

## B03's gates

**Parity tests** (`tests/Hoshin.Tests/Acceptance/Parity/`, `Category=Acceptance`): 28 tests for the 26 rows the B03 table sends to Parity. 16 fail today at the step their task fixes, and 12 pass, because the engine already matches the original there. A passing parity test must stay green: one that goes red is a regression, not a gate.

| Task | Failing parity tests |
| :--- | :--- |
| B05 | `LifecycleParityTests+StartRoutesABugToFix` |
| B06 | `LifecycleParityTests+IntentKeepsScopeAsTyped` |
| B10 | `LifecycleParityTests+PlanGivesUpAfterThree` (the unlisted-repository test; see B10's reviewer addition) |
| B11 | `LifecycleParityTests+BuildCommitsCheckedEntry`, `+BuildPrintsTheSchedule`, `+BuildRecordsRed` (with R24), `+BuildRefusesAStrangerMaster` |
| B13, B14 | `LifecycleParityTests+SingleRepoDoneOnePullRequest` (B14 item 6) |
| B14 | `FixParityTests+Fix07_AcceptRefusesAGrownPlan`, `+Fix13_AcceptStopsAtUnrecordedChanges`, `+Fix17_FinishesAStoppedAcceptance`, `+Fix19_RecordsTheEngineersEdits` |
| B15 | `FixParityTests+Fix23_RejectsADraftedFix`, `+Fix25_ReopenKeepsBuiltTasks`, `+Fix26_ReopensAnAcceptedFix`, `LifecycleParityTests+IntentRejectsAChange` |

**Eval cases** (`evals/<case>/`): 39 cases, the 37 B03 owes plus the two runner cases. Only the two runner cases have run against a model. Every case fails at `hook-trace-has-an-entry` until R-new-1 lands, because the sandbox's home folder is a git repository (`evals/README.md`, "The sandbox's home repository"). Don't spend on a case before then. Each skill task runs its own cases and records an `Evals:` trailer (R3-rule 4). The B03 table in `evals/README.md` names each case's task.

**`EvalGraderSelfTests`** (`Category=Acceptance`) runs `evals/test/*.test.mjs`: 291 checks on the case files and their graders, about 6 minutes. It is not red on purpose; it always passes. If it fails after your commit, your commit broke something it reads (a CLI subject, a commit kind, a document check), and the fix belongs in your code, not in the test.

Three reviewer additions came out of writing B03, and they are in TASKS.md:
- B05, B06 and B14 pass the engineer's change name as the skill's first argument.
- B10's Stop-time plan check enforces the system's repository list.
- R3-rule 4's exception now covers the B03 parity tests.

## Two corrections to TASKS.md you must know about

1. **R23's Problem is wrong as first written.** A typed `/hoshin:build` in a finished product repository is refused by `WorkflowEngine.Gate` before `StartHandler` can delete `run.json`. The real defect is S16: a second session that types a step another session owns takes the run over. R23 item 3 is the heart of the task; items 1 and 2 still stand. See the reviewer correction under R23.
2. **B14 has a sixth engine item.** In a single-repository workspace the review commit lands on the change branch after the review recorded its head, so `done` can never publish a light-track change. This was found while writing R21 (6), so per R3-rule 6 it already has its gate.

## Order of work

R3-rule 5 sets the order, and the round-3 index lists the tasks within each tier:

1. **R3-1, lifecycle and integrity:** R23, R22, R10, R11, R12, R13, R14, R15, R16, R24, R25, R26, R27, R28, R29, R30, R-new-1. R-new-1 sits last in the index (R3-rule 6) but belongs to this tier, and it must land before any eval runs.
2. **R3-2, enforcement:** R17 (needs R29), R18.
3. **R3-3, parity (B01, B04–B15):** don't start until B03 has landed. Its eval cases don't exist yet, so a skill task has nothing to turn green and no `Evals:` trailer to write. The engine halves are tested in C# first, but they also come after R3-1 and R3-2, because an eval that fails on an engine defect says nothing about the skill.
4. **R3-4, release:** R20. R40 is the engineer's.

One commit per task, in index order. The trailers below go in the message's last paragraph, together with `Co-Authored-By:` and any other trailer, with no blank line between them. `conventional` reads them with git's trailer parser, which only sees the last paragraph:

```
Task: R23
Test-first: <test name> failed before the change: <one-line failure>
Docs: <owner documents updated, or "none">
Gate: <the first failing acceptance test after this commit, or "passes">
Evals: <case n/3, …>
```

`Evals:` is on B05–B15 only. R40 has no `Test-first:`.

`Gate:` names the first failing test in file order, as `UserPathAcceptanceTests+R21_02_Decline`. Today that is R21 (2), so your first commits will keep naming it until R22 lands. That is expected; what must never happen is a test that passed starting to fail. Keep the full list in the pull request's Tasks table.

## Running the gates

```
dotnet build Hoshin.sln
dotnet test tests/Hoshin.Tests --filter "FullyQualifiedName!~BlackBoxConformanceTests&Category!=Acceptance"
dotnet test tests/Hoshin.Tests --filter "Category=Acceptance"
```

- The acceptance folder takes about 13 minutes. Each R21 test is its own nested class, because xUnit 2.9.3 parallelizes only across classes. Run one test with `--filter "FullyQualifiedName~R21_10_TwoSessions"` while you work, and the whole folder before each commit.
- The tests drive the shipped `hoshin.dll`, so build first. A stale build gives you a stale gate.
- R21 (9) skips a language whose toolchain is missing and fails when `CI` is set. On this machine Go is present, so the Go case runs and fails until R25.
- The harness fails a test on any swallowed `hoshin hook:` crash, not only on wrong output. If a test fails with a crash message, the crash is the defect.
- `.git/hoshin/hooks.jsonl` is written only while `run.json` exists. A test that finds an empty trace after the run ended is reading it too late, not proving a hook didn't fire.

## Set up for you before you start

- **B02 is settled** (TASKS.md, B02's reviewer notes). In WSL, .NET 10 is in `~/.dotnet`, Claude Code is logged in, and the runner works. A slash-command prompt counts as typed by the user. The smoke case waits on R-new-1. The suite runs only in WSL, never in CI: each skill task runs its own cases with `run-wsl.sh` (about $0.50 per case per run) and records them in its `Evals:` trailer.
- **The trailer check** (PR #9, on `main`): on `fix/round-3`, `conventional` fails a commit after the last reviewer commit (the table above) that lacks exactly one `Task:` from the round 3 index. It also fails a second commit for the same task, and a missing `Test-first:` (not on R40), `Docs:` or `Gate:`, or a missing `Evals:` on B05–B15. Every trailer goes in the message's last paragraph, with no blank line between them, or git doesn't see it. `gate owner` treats `conventional.yml` as a gate path.
- **The pull request** is #10, a draft titled `feat(workflow)!: ready to replace hoshin`. Keep its Tasks table current with each commit: status, commit, and the `Gate:` value. Record any gate dispute in its "Gate disputes" section. Don't mark it ready: the engineer does that when the replacement bar holds.
