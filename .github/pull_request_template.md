## What and why

<!-- One concern per pull request (CONTRIBUTING.md §1). A program pull request (one commit per task) summarizes the program here and lists its tasks below. -->

## Tasks (program pull requests only)

<!-- For a pull request that delivers a task list (e.g. docs/review/TASKS.md) as one commit per task. Delete this section otherwise. Keep it current on every push. -->

| Task | Commit subject | Status |
| :--- | :--- | :--- |
| T32 | `ci(packaging): run the packager and the full suite on Linux and Windows` | done |

## Test first

<!-- Which test did you write first, and how did it fail before the change? (CONTRIBUTING.md §6, T1) Paste the failing assertion or its one-line summary. In a program pull request, each commit's `Test-first:` trailer carries this instead; write "see commit trailers". -->

## Behavior and contract changes

<!-- What behaves differently now? Which owner documents did you update (DESIGN.md, CLIENTS.md, CHANGELOG.md, docs/KATA_MIGRATION.md)? Write "none" if nothing changed for users or clients. -->

## Design checklist (CONTRIBUTING.md §3–§4)

Every box must be ticked, or explained under it.

- [ ] **S1/S2:** each new or changed type has one responsibility, and decision logic does no IO.
- [ ] **S3:** no source file over 400 lines, and no method over 60 lines or complexity 15, was created or grown.
- [ ] **O1/O2:** no new string-typed dispatch over a closed set, and new kinds are added as new types plus registration.
- [ ] **L1/L2:** implementations and fakes keep their interface's documented contract, and nothing downcasts an abstraction.
- [ ] **I1/I2:** no interface over 10 members, and every dependency is the narrowest one that serves.
- [ ] **D1–D4:** every outside effect (process, git, file system, clock, environment, network) goes through a required constructor-injected interface. There are no `?? Default` / `?? new` fallbacks, no new services outside `HoshinServices`, and no mutable static state.
- [ ] **R1:** no rule, list or text was copied into a second place.
- [ ] **C3/C4:** no silent catch, and safety decisions fail closed.
- [ ] **Baseline:** no new entry in `tests/Hoshin.Tests/Architecture/baseline.json`, and entries for fixed violations are removed.

## Verification

- [ ] `dotnet build Hoshin.sln` has 0 warnings.
- [ ] `dotnet test tests/Hoshin.Tests` passes in full, on Linux and Windows (CI).
- [ ] At least 90% of the changed lines in `src/` are covered (CI).
- [ ] No test was skipped, filtered out, weakened or deleted to get green (or the deliberate behavior change is explained above).
