# Contributing to Hoshin.Net

These are the development rules for Hoshin.Net. They apply to every change, whether a person or an agent makes it. **None of them is optional.** A pull request that breaks a rule is not merged, whatever it fixes. If a rule blocks a change that is right, change the rule first, in its own pull request.

A rule here is checked in one of three ways:
- **[test]** an automated test or build setting fails;
- **[CI]** a workflow check fails;
- **[review]** the reviewer checks it against the pull request template.

A rule marked *(lands with Tn)* is enforced automatically once that task in `docs/review/TASKS.md` is merged. Until then, the reviewer checks it.

## 1. Branches, commits and pull requests

All three follow [Conventional Commits](https://www.conventionalcommits.org/).

- **Types:** `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci` and `chore`. A breaking change adds `!` after the scope, as in `feat(engine)!: <subject>`.
- **Scopes:** `engine`, `hooks`, `guard`, `cli`, `documents`, `changes`, `events`, `git`, `workspace`, `skills`, `packaging`, `tests`, `docs` and `ci`. Use the narrowest scope that fits, and `workflow` when a change spans several of the library's scopes.
- **Branches** are named `<type>/<short-description>` in lowercase kebab case, for example `fix/guard-scope`. Work from `docs/review/TASKS.md` adds the task ID: `fix/t01-guard-scope`.
- **Commits** are `<type>(<scope>): <subject>`, for example `fix(guard): stay silent outside hoshin workspaces`.
- **Pull request titles** use the commit format. The repository merges by squash only, and the squash commit takes the title. **[CI]**
- **Pull request descriptions** fill in every section of `.github/pull_request_template.md`, including the design checklist. **[review]**
- **One concern per pull request:** one bug or one feature. Refactoring that the change needs goes in a separate commit in the same pull request, before the behavior change, so each can be reviewed alone. **[review]**
- **Program pull requests** are the exception: a planned set of tasks, such as `docs/review/TASKS.md`, lands as one pull request with **exactly one commit per task**. The rules for those:
  - every commit is a Conventional header plus a `Task: <id>` trailer;
  - every commit builds with 0 warnings and passes the fast test suite on its own;
  - the branch keeps the per-task commits so review can go one task at a time, and the pull request is then squash-merged like any other.

  `docs/review/TASKS.md` ("Delivery") has the details. **[CI]**

## 2. How work reaches `main`

1. Branch off `main`. Never commit to `main` directly.
2. Open a pull request.
3. Merge only when CI is green on both Linux and Windows, by squash merge.

`main` is protected: changes arrive by pull request, with no force pushes, linear history, and the `ci gate` and `conventional names` checks required on a branch that is up to date with `main`.

### 2.1 Bypass and the acceptance gate

The repository has one owner, and the changes are made by AI agents (Claude Code, Antigravity) working with the owner's credentials. GitHub can't tell the owner and the agents apart, so the protection stops accidents, not people, and administrators can merge past a failing check. These rules are the owner's commitment:

- **Docs emergencies are the only bypass.** That means a pull request that changes only documentation (Markdown outside `src/`, `tests/`, `skills/`, `packaging/` and `.github/`), when a required check can't run or fails for a reason unrelated to the change, such as a GitHub outage. Its description says what happened.
- **Code never gets the bypass.** That covers source, tests, skills, packaging and CI workflows. A red check on code is fixed, not bypassed, however urgent the change is.
- **The acceptance gate (`tests/Hoshin.Tests/Acceptance/`) is changed only by the reviewer.** Before merging any pull request, run `git diff origin/main...HEAD -- tests/Hoshin.Tests/Acceptance/`. It must be empty, or show only changes the reviewer made.

## 3. Design rules (SOLID)

Hoshin.Net is a library plus a thin CLI. Its value is that the mechanical part of the workflow is deterministic and testable. These rules keep it that way.

### 3.1 How the rules apply to existing code

The code already has violations. They are listed in `tests/Hoshin.Tests/Architecture/baseline.json`.

- **[test]** A change may not add a violation. The architecture tests fail on any violation that isn't in the baseline.
- **[test]** A change that fixes a violation removes its baseline entry. The tests also fail on a baseline entry that no longer violates, so the baseline only shrinks.
- **[review]** When you add behavior to a file that has a baselined violation, put the new code in a new, focused type, and don't grow the file. "It was already like that" is not a reason to make it worse.

### 3.2 Single responsibility

- **S1. One reason to change per type.** A type does one of these things, and only one:
  - **decides** (pure logic over values);
  - **talks to the outside** (process, git, file system, network, clock, environment);
  - **adapts a protocol** (a harness payload, the CLI argument syntax);
  - **composes** other types.

  For example, `WorkflowEngine` decides, `AllowListedCommandRunner` talks to processes, `HarnessAdapter` adapts, and `HoshinServices` composes. **[review]**
- **S2. Decisions are pure.** Code that decides a workflow outcome (the engine, Red classification, gates, document validation) takes values and returns values. It does no file, process, git, clock or environment access. **[test]** for `Engine/WorkflowEngine.cs` and `Engine/RedClassifier.cs`; **[review]** elsewhere.
- **S3. Size limits:**
  - a source file has at most **400 lines** **[test]**;
  - a method has a cyclomatic complexity of at most **15** **[test]** (through analyzer CA1502 and `CodeMetricsConfig.txt`);
  - a method has at most **60 lines** **[review]**.

  A type that needs more is doing more than one thing; split it.

### 3.3 Open/closed

- **O1. Closed sets are types, not strings.** Step kinds, document kinds, verdict outcomes, harnesses, and event names are enums or sealed record hierarchies. Code never compares a string to a literal to decide which kind something is, except when parsing it at the edge. JSON stays the same through `JsonStringEnumConverter`. **[review]**; **[test]** for `CurrentStep.Kind` and document names *(lands with T36)*.
- **O2. Extend by adding a type.** A new document kind, lifecycle event, harness or skill is added by implementing an interface (for example `IEventStep` or `IHookAdapter`) and registering the implementation in the composition root. A change that has to edit a `switch` in three places to add a kind is redesigned first. **[review]**

### 3.4 Liskov substitution

- **L1. Implementations keep the contract.** Every implementation of an interface, test fakes included, follows the interface's documented behavior: what it returns for "not found", what it throws, whether it may return null, and whether it is safe to call concurrently. Document these on the interface. **[review]**
- **L2. No type checks on abstractions.** Code that holds an interface does not downcast it (`if (x is ConcreteType c)`) to reach behavior the interface lacks. Add the member to the interface, or depend on a different interface. **[review]**

### 3.5 Interface segregation

- **I1. Small, role-shaped interfaces.** An interface has at most **10 members**, and serves one role. `IGitClient` (34 members) and `IWorkflowRunner` (13) are baselined violations. **[test]**
- **I2. Depend on what you use.** A type takes the narrowest interface that gives it what it calls. In particular:
  - no command takes `HoshinServices`;
  - no type takes a service only to pass it to another.

  **[test]** for commands; **[review]** elsewhere.

### 3.6 Dependency inversion

- **D1. Every outside effect goes through an injected interface:** processes, git, the file system, the clock (`TimeProvider`), environment variables, `gh` and the network. **[review]**; **[test]** for `DateTimeOffset.UtcNow` / `DateTime.Now` outside the clock adapter.
- **D2. Dependencies are required constructor parameters.** No optional dependency parameters (`IFoo? foo = null`), and no `?? X.Default` or `?? new X()` fallbacks. A forgotten dependency must be a compile error, not real git in a unit test. **[test]**
- **D3. One composition root.** `src/Hoshin.Cli/HoshinServices.cs` is the only place that creates services. Library types never `new` another service, and never read a `static Default`. `static Default` properties exist only where an external caller of the NuGet package needs a ready-made instance, and each one is documented as such. **[test]**
- **D4. No mutable static state.** Static fields are `readonly` and hold immutable values (compiled regexes, frozen sets). **[test]**

### 3.7 One owner per rule

- **R1.** Every rule, list or text has exactly one definition in code, and other code reads it from there. That covers protected paths, tool names, required headings, default branches, timeouts, test patterns and procedure texts. Copying one into a second place is a bug, even when the copies match today. **[review]**; **[test]** for the hook manifest matchers *(lands with T36)*.

## 4. Code rules

- **C1. Clean build.** `dotnet build` has 0 warnings with `TreatWarningsAsErrors` and the analyzers on. A suppression needs a comment saying why. **[CI]**
- **C2. Nullable is honest.** Don't use `!` to silence a value that can really be null. Handle the null.
- **C3. No silent catch.** No empty `catch {}`, and no `catch (Exception)` that neither logs nor rethrows. Catch the specific exception you expect, and say what happens next. **[test]**
- **C4. Fail closed on safety decisions.** When the guard, an approval check or a Red verdict can't decide (bad input, missing state), the answer is "no", with a message. The exception is availability: a hook must never crash a session. So a hook returns a deny or block reply with an explanation, rather than throwing. **[review]**
- **C5. No shell.** Processes start with `UseShellExecute = false` and an argument list. Command text is never concatenated. **[review]**
- **C6. Secrets stay out of child processes.** Every child process is started with the allow-listed environment (`AllowListedCommandRunner`), except git calls that need credentials to push, which use the push allow-list. **[review]**
- **C7. Bounded external calls.** Every process, git call and file lock wait has a timeout, and IO-bound public methods take a `CancellationToken`. **[review]**
- **C8. Atomic state writes.** Files under `.git/hoshin/` are written to a temp file and moved into place, under the shared file lock when other processes may write them. **[review]**
- **C9. Regexes are generated.** Use `[GeneratedRegex]` for fixed patterns, and give regexes built from data a match timeout. **[review]**
- **C10. Git output is parsed with `-z`** wherever paths are read from it. **[review]**
- **C11. Language and formatting:** US English in code, comments and documents. Formatting follows `.editorconfig`. **[CI]**

## 5. Architecture invariants

These come from `AGENTS.md`, and they are tested where possible.

1. `Hoshin.Workflow` references only `Markdig` and `Gherkin`. Anything else lives in a separate assembly. **[test]**
2. Harness-specific code (Claude Code, Antigravity) lives only in `src/Hoshin.Cli/Hooks` and `src/Hoshin.Cli/Commands`. **[test]**
3. Hoshin documents contain no YAML. YAML appears only in negative tests and in the generated `SKILL.md` front matter. **[test]**
4. `skills/*/procedure.md` contains judgment guidance only: no YAML, no CLI commands, no harness tool names. **[test]** (`AllSkills_PassStrictLint_NoYamlNoCliNoHarnessToolNames`)
5. No `.agents/hooks.json` or `.claude-plugin/` in the repository root. This repository is developed outside the hoshin workflow. **[review]**

## 6. Tests

- **T1. Test first.** Write the test, run it, and see it fail for the reason the change addresses. Then write the code. A bug fix starts with a test that reproduces the bug. **[review]**: the pull request says which test failed first.
- **T2. Never weaken a test to get green.** Don't skip, ignore, filter out, loosen or delete a test to make a change pass. A test may change only when the behavior it asserts is deliberately changing, and the pull request says so. **[review]**
- **T3. Coverage of what you change.** At least **90%** of the lines a change adds or modifies in `src/` must run in a test. Branch coverage is reported, not gated. Don't hide lines from coverage (`[ExcludeFromCodeCoverage]` is allowed only on generated code). **[CI]**
- **T4. Pick the right kind of test:**

  | Kind | Location | Use for |
  | :--- | :--- | :--- |
  | Unit | `tests/Hoshin.Tests/Unit/` | One type, with fakes for its interfaces; most tests. |
  | Black-box | `tests/Hoshin.Tests/BlackBox/*.test.mjs` | The CLI contract (`CLIENTS.md`), against the built `hoshin.dll`. |
  | Golden examples | `tests/Hoshin.Tests/Examples/<kind>/2/` | Document readers. Every reader rule has a `.md` plus `.expected.json` pair. |
  | End-to-end hooks | `Unit/DualHarnessHookIntegrationTests.cs`, `Unit/AuthoringAndReviewHookTests.cs` | One scenario per lifecycle path. Don't add assertions to an existing scenario for a new edge case; write a focused test. |
  | Architecture | `tests/Hoshin.Tests/Architecture/` | The rules in §3–§5. |

- **T5. One behavior per test**, named for that behavior: `Guard_AllowsPush_OutsideHoshinWorkspace`, not `Guard_Works`.
- **T6. Isolation.** A test that needs a folder gets it from `TempDir` (`Unit/TestWorkspaceHelper.cs`) or `tempDir` (`BlackBox/helpers/temp.mjs`), which clean up after themselves. Tests never touch the developer's real repositories, home directory or global git config. A test that needs a git repository builds it with `TestGitFixture`.
- **T7. No timing guesses.** Don't use `Thread.Sleep` to wait for something to happen. Wait on the condition with a timeout.
- **T8. Frozen fixtures.** Files under `tests/Hoshin.Tests/Fixtures/` are never edited to make a test pass. A new real document gets a new fixture.
- **T9. Both operating systems.** Tests pass on Linux and Windows. A test that can't run on one OS says why, in its skip reason. That is allowed only for genuinely OS-specific behavior, never as a way to get green. **[CI]**

To run the tests:

```bash
dotnet build Hoshin.sln
dotnet test tests/Hoshin.Tests --filter "FullyQualifiedName!~BlackBoxConformanceTests"   # fast
dotnet test tests/Hoshin.Tests                                                           # full (~7 min)
```

## 7. The CLI contract

- `CLIENTS.md` lists what external tools (the Kata Workbench, CI) read from the CLI. Removing, renaming or rewording anything it lists is a breaking change: `!` in the commit, plus a `CHANGELOG.md` entry and a note in `docs/KATA_MIGRATION.md`. **[test]** `BlackBox/clients.test.mjs` fails when the file and the program disagree.
- Exit codes: `0` passes, `1` means problems were found, `2` means the command could not run. Hooks (`hoshin hook …`, `hoshin guard`) always exit `0`, and report errors on stderr.

## 8. Skills

- A procedure in `skills/<name>/procedure.md` describes judgment only. Sequencing, gating, commits and publishing belong to the library.
- A change to a skill procedure, or to library behavior a skill relies on, needs an eval case. *(The eval harness lands with EPIC-B; until then, describe the manual check in the pull request.)*
- `packaging/package.ps1` generates the `SKILL.md` wrappers. `accept` and `done` are user-only (`disable-model-invocation: true`). **[test]**

## 9. Releases

- The version lives in one place, `Directory.Build.props` (`VersionPrefix`). The packager writes it into `plugin.json`.
- Bump it by semantic versioning, and add a `CHANGELOG.md` entry naming every new command, option and field exactly.
- Run `packaging/package.ps1` and the full test suite on both operating systems before tagging.

## 10. Documents

Every fact has one owner. Other documents link to the owner instead of restating it, and a pull request that changes behavior updates the owner in the same pull request. **[review]**

| Owner | Owns |
| :--- | :--- |
| `DESIGN.md` | How the system works: architecture, the state machine, Red/Green, the hook protocol, state files. *(It also owns behavior until `docs/GRAMMAR.md` lands with T37.)* |
| `CLIENTS.md` | What external tools may depend on in the CLI. It defines no behavior. |
| `CONTRIBUTING.md` | How to change Hoshin.Net (this file). |
| `AGENTS.md` | Pointers and invariants for coding agents. It defers to this file for rules. |
| `README.md` | What a user needs to install and use it, with links to the rest. |
| `CHANGELOG.md` | What each release changed. |
| `docs/KATA_MIGRATION.md` | Upgrade steps for external clients. |
| `docs/review/*` | A dated review and its task list. They describe the code at a point in time, never current behavior. |

Formatting:
- Markdown has one line per paragraph, with no hard wraps.
- Diagrams use Mermaid.
