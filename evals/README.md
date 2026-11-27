# Evals

The LLM eval suite for Hoshin.Net's Claude Code bundle (TASKS.md, round 3, B02 and B03). It judges what only a model can get right: triage, interviews, the content of a spec, plan or review, and when to refuse. What the engine does (commits, acceptance order, pins, red, the schedule, publishing) is checked by C# parity tests under `tests/Hoshin.Tests/Acceptance/Parity/` instead, because an LLM run is the most expensive and least reliable way to check a mechanism.

The suite belongs to the reviewer (R3-rule 2). The implementer adds run records under `results/` and nothing else.

Each folder holds one case: `case.yaml` (the prompt and its graders) and `fixture.sh` (the workspace the case starts in). `shared/workspace.sh` is the base workspace most fixtures source. Case files are YAML because `claude plugin eval` reads only `case.yaml` or `prompt.md`; AGENTS.md invariant 4 lists them as its one exception.

## Running them

`claude plugin eval` needs a shell sandbox, so the suite runs on Linux or in WSL, not on native Windows. Each case costs about $0.50 per run.

```bash
evals/run.sh                              # builds the bundle, then runs every case
evals/run.sh --case runner-reaches-the-hooks --runs 1
```

`run.sh` does three things the bare command doesn't:

- **It tests the built bundle, not the source tree.** It builds `packaging/package.ps1` into `evals/.bundle/` (gitignored, and never wiped by the default package run), and stages it as the run's target with the cases below it (`.bundle/target/evals/`). A case names no `plugins:`: the command loads a plugin named in a case only from inside that case's own folder. Pass `--no-build` to reuse a bundle built elsewhere.
- **It refuses to start without a .NET 10 runtime.** Every hook runs `dotnet lib/hoshin.dll`. A hook that can't start fails without blocking, so without this check a case could pass `skill-fired` while nothing ran. It also starts the CLI once, the way a hook does.
- **It sets `TMPDIR` outside `/tmp`.** The sandbox denies writes under `/tmp`, and the hooks create folders under `TMPDIR`.

It caps the cost with `HOSHIN_EVAL_MAX_COST` (US dollars, default 70: a full run of 39 cases at 3 runs each is about $58) and writes each run to `results/<timestamp>/` (`results.json`, `report.html`).

On Windows, build the bundle there and run in WSL:

```bash
pwsh packaging/package.ps1 -OutputRoot evals/.bundle
MSYS_NO_PATHCONV=1 wsl.exe -d Ubuntu -- bash -l /mnt/<drive>/<path>/Hoshin.Net/evals/run-wsl.sh --case <name> --runs 1
```

`run-wsl.sh` moves Docker Desktop's symbolic links out of `~/.docker` for the run (the sandbox refuses Bash while one is there) and puts a per-user .NET (`~/.dotnet`, or `HOSHIN_DOTNET`) first on `PATH`. WSL needs .NET 10, `bubblewrap` and `socat`.

The suite runs only in WSL on the engineer's machine, never in CI (decided by the engineer, 2026-11-27): the repository holds no API key, and a run spends real money. A recorded run for the replacement bar (item 2) is committed under `results/<timestamp>/`.

## Rules every case follows

- **The hook trace must exist.** Every case has a `hook-trace-has-an-entry` grader that reads the master's `.git/hoshin/hooks.jsonl`. The trace is written only while a run is active, so a case whose hooks never ran fails there, whatever its other graders say.
- **Agent is allowed.** The original suite withheld the `Agent` tool so the build's work stayed in the graded trace. Hoshin.Net's build dispatches every task through `Agent`, and its hooks judge the subagent's result, so a build case needs it. Graders of build cases read commits and the hook trace, not the subagent's tool calls.
- **Commits are read from the reflog.** The sandbox turns git hooks off, so a grader reads `<repo>/.git/logs/HEAD` (or `.git/worktrees/<change>/logs/HEAD` for a change's copy): one line per commit, ending in `commit: <subject>`. The hooks' subjects are `docs(<scope>): start change <change>`, `draft intent|spec|plan <change>`, `draft fix <change>`, `accept intent|spec|plan <change>`, `log T<n> <change>`, `review <change>` and `reject intent <change>`, and the library makes every one of them. A fixture that commits through the CLI's `commit --kind` gets that kind's subject instead, which differs for three kinds: `spec` and `plan` write `write spec` and `write plan`, where the hooks write `draft spec` and `draft plan`, and `review` writes `record review <n> of <change>`, where the hooks write `review <change>`. A grader of a run reads the hooks' subjects. "Committed through the program" becomes: the subject is the library's, and `no-hand-commit` sees no `git commit` typed by the model (the guard denies it during a run anyway).
- **Documents are read as JSONC front matter.** A grader matches `"status": "draft"`, not `status: draft`.

## Typed commands

Hoshin.Net refuses the human-only commands (accept, done, decline, reject, reopen and `fix --accept`) when the model invokes them, which is how a plain-language prompt reaches them. Whether a case prompt that starts with a slash command reaches the hooks as typed by the user is not documented, so `runner-typed-command-is-the-users` probes it: its `typed-as-user` grader passes only when the hooks saw a `UserPromptExpansion` event.

**Answer: yes** (2026-11-27, Claude Code 2.1.308, in WSL). A prompt that starts with `/hoshin:intent` reached `hook start` as a `UserPromptExpansion` event, before any `Skill` call, so a case may type a human-only command as its prompt's first line. No case does yet: the typed steps are mechanism, so the C# parity tests drive them (B03 item 3), and a case types one only when a model step follows it.

## The sandbox's home repository (R-new-1)

The sandbox's home folder is an empty git repository, and the case folder sits inside it. Until R-new-1 lands, the hooks take that repository for the session's own and answer every skill with "There is no change plan in this repository", so every case fails at `hook-trace-has-an-entry`, whatever the skill does. The answer above comes from one diagnostic run whose fixtures deleted that repository. That run isn't a record and wasn't kept. No committed fixture deletes it: an engineer with a dotfiles repository at `~` meets the same defect, and the smoke case is its eval-side gate.

## Where each original case went (B03)

The original plugin's suite (`hoshin/evals`, at `hoshin@47f9d7b`) has 56 cases. TASKS.md says 60; the count is 56. Every one has a row. **LLM** means it is rewritten here. **Parity** means it becomes a C# test under `tests/Hoshin.Tests/Acceptance/Parity/` that drives hook payloads, the human-only steps as typed-user payloads. **Dropped** names the accepted difference that explains it. A case split across two destinations has both.

**Status: approved by the engineer, 2026-11-26, with both drops.** Rows 9 and 10 are listed under TASKS.md's "Accepted differences".

| # | Original case | Destination | Task | Why |
| ---: | :--- | :--- | :--- | :--- |
| 1 | build-commits-checked-entry | Parity | B11 | A built, unlogged task gets its log entry committed only in the master: engine (B11 item 5). |
| 2 | build-logs-in-the-change-copy | Parity | B11 | The log lands in the change's copy, and the engineer's checkout is untouched: resolution, not judgment. `--master` itself is an accepted difference. |
| 3 | build-prints-the-schedule | Parity | B11 | The schedule message names the waiting repositories and their blockers (B11 item 6). |
| 4 | build-records-red | Parity | B11, R24 | Hoshin.Net's engine observes and records red itself; the model never runs `red record`. |
| 5 | build-refuses-a-stranger-master | Parity | B11 | A second clone of the master is never taken as the master (B11 item 7). |
| 6 | done-refuses-an-unreviewed-change | Parity | B13 | `change.done` refuses before anything leaves the machine; the hook says review comes first. |
| 7 | fix-accept-refuses-a-grown-plan | Parity | B14 | The boundary is rechecked at a typed `fix --accept` (B14 item 1). |
| 8 | fix-accept-refuses-a-rejected-fix | Parity | B14, B15 | A typed accept of a rejected fix says the change is closed and commits nothing. |
| 9 | fix-accept-refuses-several-tests-in-a-task | Dropped | B14 | In the original the model counts new behaviors at acceptance. Hoshin.Net's acceptance is typed and has no model ("Advance acceptance" row), so nothing reads the plan's prose at that step. The same rule is judged at drafting (`fix-refuses-a-large-change`) and at review (`review-reports-two-behaviors-in-a-fix-task`). Accepted difference: "No model at a typed `fix --accept`". |
| 10 | fix-accept-refuses-two-behaviors-in-one-task | Dropped | B14 | As row 9. |
| 11 | fix-accepts-a-bug-fix | LLM + Parity | B14 | LLM: names the root cause and drafts three documents that pass the check, with `track: fix`, and no product code. Parity: the typed accept commits intent, spec and plan acceptances in order, with pins that hold. |
| 12 | fix-accepts-a-task-with-a-protecting-test | Parity | B14, R24 | A typed accept counts one new failing test per task; a protecting test is not a second behavior. |
| 13 | fix-accept-stops-at-unrecorded-changes | Parity | B14 | A typed accept over an uncommitted hand edit records the edit as a draft and accepts nothing (B14 item 4). |
| 14 | fix-changes-refused-at-boundary | LLM | B14 | The model sees that the engineer's change needs a second repository, refuses before writing, and recommends intent. |
| 15 | fix-feedback-refused-at-boundary | LLM | B14 | As row 14, from written feedback. |
| 16 | fix-finishes-an-uncommitted-draft | Parity | B14 | A stopped draft is finished: Stop commits the three written documents in one draft commit and accepts nothing. |
| 17 | fix-finishes-a-stopped-acceptance | Parity | B14 | A typed `fix --accept` over a half-done acceptance finishes it in order. |
| 18 | fix-follows-written-rules | LLM | B14, B01 | Reads the app's written rules, links included, so the task lists CHANGELOG.md. |
| 19 | fix-records-the-engineers-edits | Parity | B14 | The engineer's edit to a draft is kept and committed, not rewritten (B14 item 4). |
| 20 | fix-redrafts-from-feedback | LLM | B14 | The redraft carries the new scenario, and the boundary holds (B14 item 3). |
| 21 | fix-refuses-a-large-change | LLM | B14, B01 | Applies the task cap to a feature described as a quick fix. |
| 22 | fix-refuses-a-rule-change | LLM | B14, B01 | Names the confirmed context rule the "fix" would change. |
| 23 | fix-rejects-a-drafted-fix | Parity | B15 | A typed `fix --reject` commits the rejection and abandons the change. |
| 24 | fix-reopen-broken-pin-waits | Parity | B15 | Reopening on a broken pin stops and waits instead of regenerating. |
| 25 | fix-reopen-keeps-built-tasks | LLM + Parity | B14, B15 | LLM: the redraft leaves T1 exactly as written. Parity: the reopen keeps T1 built (R21 (5) covers the full pipeline). |
| 26 | fix-reopens-an-accepted-fix | LLM + Parity | B14, B15 | LLM: the feedback is applied to the spec and plan. Parity: spec and plan back to draft, intent kept, one reopen commit. |
| 27 | fix-waits-for-workbench-acceptance | Parity | B14 | A model-invoked accept is refused, whatever the prompt's wording, so the mechanism guarantees what the original's skill had to remember. |
| 28 | ignores-system-plan-template | LLM | B01 | Writes the plan in the plugin's layout, names the unused system template, and commits only the plan. |
| 29 | intent-asks-scope-keeps-it-as-typed | LLM | B06 | Asks for the scope and keeps `PROJ-123` exactly as typed. |
| 30 | intent-keeps-scope-as-typed | LLM + Parity | B06 | LLM: doesn't ask when the scope came with the request. Parity: a `scope:` argument is kept as typed, in the brief and the commit subject (B06 item 1). |
| 31 | intent-rejects-a-change | Parity | B15 | A typed reject commits on the change branch and removes the copy (R21 (5) covers it end to end). |
| 32 | plan-applies-an-outline-correction | LLM | B10 | Applies the engineer's outline correction and records it under Engineer direction. |
| 33 | plan-cites-outline-examples | LLM | B10 | Cites the spec's Scenario Outline instead of copying its rows. |
| 34 | plan-follows-written-rules | LLM | B10, B01 | Reads written rules, links included, and doesn't restate a style rule. |
| 35 | plan-gives-up-after-three | Parity | B10 | Stop's check-and-block loop gives up after its cap and commits nothing. |
| 36 | plan-keeps-whole-app-checks-out-of-tasks | LLM | B10 | Keeps whole-app checks out of tasks and names them under Checks at build finish. |
| 37 | plan-stops-on-direction-against-the-spec | LLM | B10 | Declines to write when the direction contradicts the spec, and says to reopen it. |
| 38 | plan-takes-engineer-direction | LLM | B10 | Lands the engineer's direction in the plan and records where. |
| 39 | review-commits-an-interrupted-review | LLM | B12 | Offers the uncommitted review as it is, commits it on a yes, and dispatches no reviewers (B12 item 4). |
| 40 | review-commits-findings | LLM | B12 | Records a convention finding that cites the repository's rule. |
| 41 | review-ignores-a-stricter-protecting-test | LLM | B12 | A stricter protecting test is not a new behavior. |
| 42 | review-reads-red-from-the-program | LLM | B12 | Records a spec finding at the build log's `red:` line when red was not observed. |
| 43 | review-reports-two-behaviors-in-a-fix-task | LLM | B12 | Reports a light-track task with two new behaviors. |
| 44 | setup-refreshes-instructions | LLM | B09 | Instructions-only mode: replaces the marked block and keeps the text around it. |
| 45 | setup-refuses-a-deprecated-hook-point | LLM | B09 | Refuses a hook on `task.done`, names the points that stay, writes nothing. |
| 46 | setup-sets-team-naming | LLM | B09 | Asks for both patterns, refuses the invalid one, commits the marker. |
| 47 | setup-writes-workspace-instructions | LLM | B09 | Writes the instructions and appends the block without touching the engineer's notes. |
| 48 | single-repo-done-one-pull-request | Parity | B13 | In the single-repository layout, `change.done` runs `build.finish` only for the touched app and opens one pull request (R21 (6) covers it end to end). |
| 49 | spec-finds-a-change-in-its-copy | Parity | B07 | The spec brief points at the change's copy; the commit lands there and the checkout gains none. |
| 50 | spec-fixes-then-commits | Parity | B07 | Stop checks the spec before it commits, and commits only the spec. |
| 51 | spec-in-copy-reads-the-workspace | LLM | B07 | Reports the stale context file and reads the product repository's code from inside the copy. |
| 52 | start-drafts-a-fix-for-later-acceptance | LLM | B05 | Start hands off to fix, which drafts in one commit and accepts nothing. |
| 53 | start-offers-fixed-choices | LLM | B05 | Shows the verdict and exactly the light track's three choices, in order. |
| 54 | start-offers-pipeline-choices | LLM | B05 | Shows the verdict, the broken rule, and exactly the pipeline's two choices. |
| 55 | start-routes-a-bug-to-fix | LLM + Parity | B05 | LLM: routes the bug to fix. Parity: the advance acceptance becomes a typed accept ("Advance acceptance" row). |
| 56 | start-routes-a-feature-to-intent | LLM | B05 | Routes the feature to the pipeline and gives the product owner's command instead of interviewing. |

Cases the original never had, which Hoshin.Net's skills now need (B03 item 5):

| Case | Destination | Task | What it checks |
| :--- | :--- | :--- | :--- |
| review-dispatches-per-repository | LLM | B12 | A two-repository review dispatches one reviewer per repository plus one across them, and merges their findings into one appended section. |
| start-hands-off-to-fix-in-one-change | LLM | B05 | After start's light-track verdict, the same session drafts the fix and the workspace holds one change. |
| context-builds-a-repository-file | LLM | B08 | A first context run writes a repository's file with tagged rules, asks only where the code is unclear, and the library commits it. |
| context-refreshes-a-stale-file | LLM | B08 | A refresh carries unchanged rules forward, retires a changed one, and reports the counts. |
| runner-reaches-the-hooks | LLM | B02 | The runner's smoke case: the built bundle's hooks run inside the eval. |
| runner-typed-command-is-the-users | probe | B02 | Answers "Typed commands" above; not counted against the bar. |

Totals: of the 56, 33 go to LLM (5 of them also to Parity), 21 to Parity only, and 2 are dropped.

## Grader self-tests

`node --test "evals/test/*.test.mjs"` checks the graders themselves, in the form the black-box suites under `tests/Hoshin.Tests/BlackBox/` use. They cost nothing, so the fast suite runs them on every commit (`EvalGraderSelfTests`), against the test build of `hoshin.dll`. They need bash (Git Bash on Windows), git and dotnet.

- `cases.test.mjs` checks every case's shape (the narrow YAML subset the case files keep to, so a broken file is found before a paid run), that every case carries `hook-trace-has-an-entry`, that no grader reads the original's mechanism, and that this README's table, the case folders, the parity tests (`[Trait("Original", "<case>")]`) and TASKS.md's accepted differences agree.
- `<case>.test.mjs`, one per case, tries each of its graders on both sides: on what a passing run leaves and on what a failing run leaves. It builds the case's real fixture, and makes the commits a run would make through the program (`withHelpers`), never by writing git history by hand. `fix-refuses-a-rule-change.test.mjs` is the model.
- `lib.mjs` holds the helpers: `graders`, `matches`, `fixture`, `withHelpers`, `target`, `traceLine`.

A fixture reaches the state its case starts from through the program as well: `shared/workspace.sh` gives it `hoshin`, `change_start`, `copy_of` and `commit_doc`, so subjects, pins and the change's copy are the engine's own.
