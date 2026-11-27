# Round 3 handoff: from the reviewer's session to the implementing session

Written 2026-11-26 by the reviewer's session, which wrote round 3's task list and its gates. Read this, then `docs/review/TASKS.md` from "The replacement bar" to the end. TASKS.md is the authority; this file only says where things stand and what will trip you.

## Who you are, and the one rule you can't break

You implement every round-3 task except R21, B02, B03 and R40. You have not seen the gates being written, and that is the point (R3-rule 1).

- Never write, edit, skip, filter, rename or re-trait anything under `tests/Hoshin.Tests/Acceptance/` (that includes `Harness/`) or `evals/`. The only exception is new run records under `evals/results/`.
- Don't change CI or `evals/run.sh` so that a gate stops running.
- If a gate looks wrong, add it to the "Gate disputes" section of the pull request with the evidence, and keep working. The reviewer decides.

`.github/CODEOWNERS` on `main` (PR #7) names the reviewer as owner of the gates, `evals/` (but not `evals/results/`), `ci.yml`, `evals.yml` and itself. Branch protection doesn't require code-owner review yet, and you push under the same GitHub account as the engineer, so no GitHub setting can tell your commits from theirs. The reviewer checks `git log origin/main..HEAD -- tests/Hoshin.Tests/Acceptance evals .github` before merge, and any commit there that isn't R21, B02, B03 or a reviewer's `R-new` gate is reverted. Treat the rule as a hard wall.

## What is on the branch

`fix/round-3`, from `main` at `08ceece` (PR #6). Pushed and in sync with `origin`.

| Commit | Task | What |
| :--- | :--- | :--- |
| `82fb3d4` | R21 | The user-path acceptance gate. The R0 harness moved to `tests/Hoshin.Tests/Acceptance/Harness/`, shared by both gates; R0 still passes 3 of 3. |
| `f11fd78` | B02 | The eval runner (`evals/run.sh`, `evals/run-wsl.sh`, `.github/workflows/evals.yml`), the smoke case and the typed-command probe. Not run yet. |
| `5d47acd` | B02 | `.gitattributes`: shell scripts stay LF on every checkout. |
| this commit | — | This handoff, the index statuses, and B14 engine item 6. |

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

Each test's `<summary>` in `UserPathAcceptanceTests.cs` names its R21 item and its task. Read the test before you start its task: it is the most exact statement of "done" you have.

## Two corrections to TASKS.md you must know about

1. **R23's Problem is wrong as first written.** A typed `/hoshin:build` in a finished product repository is refused by `WorkflowEngine.Gate` before `StartHandler` can delete `run.json`. The real defect is S16: a second session that types a step another session owns takes the run over. R23 item 3 is the heart of the task; items 1 and 2 still stand. See the reviewer correction under R23.
2. **B14 has a sixth engine item.** In a single-repository workspace the review commit lands on the change branch after the review recorded its head, so `done` can never publish a light-track change. This was found while writing R21 (6), so per R3-rule 6 it already has its gate.

## Order of work

R3-rule 5 sets the order, and the round-3 index lists the tasks within each tier:

1. **R3-1, lifecycle and integrity:** R23, R22, R10, R11, R12, R13, R14, R15, R16, R24, R25, R26, R27, R28, R29, R30.
2. **R3-2, enforcement:** R17 (needs R29), R18.
3. **R3-3, parity (B01, B04–B15):** don't start until B03 has landed. Its eval cases don't exist yet, so a skill task has nothing to turn green and no `Evals:` trailer to write. The engine halves are tested in C# first, but they also come after R3-1 and R3-2, because an eval that fails on an engine defect says nothing about the skill.
4. **R3-4, release:** R20. R40 is the engineer's.

One commit per task, in index order, with these trailers:

```
Task: R23
Test-first: <test name> failed before the change: <one-line failure>
Docs: <owner documents updated, or "none">
Gate: <the first failing acceptance test after this commit, or "passes">
Evals: <case n/3, …>            # B05–B15 only
```

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

## Waiting on the engineer (not on you)

- **B03:** approve the sorting table in `evals/README.md`, including the two proposed drops (rows 9 and 10, the "count behaviors at acceptance" cases). The reviewer's session then writes the cases.
- **B02:** permission to spend about $1 on the two runner cases. .NET 10 (SDK 10.0.404, runtime 10.0.15) is installed in WSL `~/.dotnet`. The no-runtime half of B02's done-when holds in WSL: with no `dotnet` reachable, `run-wsl.sh` stops with exit 1 before calling `claude`. With the runtime present, the startup probe passes (`scope --json demo` prints `{"problems":[]}`), and `claude` 2.1.308, `bwrap` and `socat` are on PATH. Not yet run: the smoke case itself, and the typed-command probe. Until it is known, whether a slash-command prompt counts as typed by the user stays open.
- **The trailer check:** the conventional workflow checks trailers only on `fix/review-findings`. The engineer extends it to `fix/round-3` with `R` and `B` IDs, requires `Gate:`, and requires `Evals:` on B05–B15, before your first task commit lands.
- **The pull request:** the engineer opens it as a draft titled `feat(workflow)!: ready to replace hoshin`, with the Tasks table, "Gate disputes" and the "Replacement bar" checklist.
