# Hoshin.Net: a .NET workflow driven by harness hooks

Nov 15, 2026 · @Satishkrishna Pilla

## Summary

Hoshin.Net rewrites hoshin's backbone in .NET: one workflow library and one `hoshin` program that drives both a Claude Code plugin and a Google Antigravity (`agy`) bundle through hooks alone. Spikes on 2026-11-15 confirmed the core design on Claude Code; Spike 5 verifies the Antigravity hook adapter; the results are under Spike results.

The library holds the steps, the typed contracts, the run's state, and `Next(state)`, the one function that decides what happens next. Step order, retry limits and gate rules live only there.

The skill bodies say only what a role does, using no harness-specific tool names. Hooks call `hoshin`, which normalizes the harness payload (Claude Code or Antigravity), reads the state, runs the steps that need no agent (tests, red, checks, publish) and hands the model the next step, so the model never sequences commands.

hoshin's documents stay Markdown, with every structured part in JSONC instead of YAML: front matter, task fields, build-log entries, review findings and `change.jsonc`.

## Goals and non-goals

Goals:

- One definition of the workflow: step order, retries, gates and verdicts live in one library, tested once.  
- First-class, Rollout 1 support for both **Claude Code** and **Google Antigravity** started from a conversation, whose skill bodies never mention the program, the host harness, or the workflow.  
- One `hoshin` program that is the hook command for both harnesses and the command line other tools call (the Kata Workbench, CI).  
- Verdicts never depend on what an agent reports: tests, red and commit checks are run by the library.  
- No YAML in `Hoshin.Net`: documents are Markdown with JSONC, and configuration is JSON or JSONC.

Non-goals:

- A host on Microsoft Agent Framework. It is a separate project that references the `Hoshin.Workflow` package; nothing in Hoshin.Net or the harness plugins depends on it.  
- A workflow DSL or a graph described as data. The order is C\# in `Next`.  
- Moving intent, spec and plan authoring into an automated pipeline. Those stay conversational skills that produce the workflow's input, gated by hooks.  
- Credential isolation on an engineer's laptop. The guard keeps out honest mistakes; it is not a sandbox.

## Background

Hoshin today is a Claude Code plugin with ten skills and a TypeScript program, `D:/workspaces/hoshin/tools/hoshin.ts`, of about 7,600 lines. `D:/workspaces/hoshin/tools/GRAMMAR.md` specifies every command, rule and refusal. The skills drive: each one tells the model which program command to call, in which order.

That is the cost Hoshin.Net removes. The build skill is 13.5 KB, mostly orchestration (locate and gate, resume, seven check parts, commits, events), and every skill also reads the 25.6 KB `D:/workspaces/hoshin/references/workspace.md`. Eval cases such as `build-records-red` and `build-commits-checked-entry` exist because the model does not always call the right command at the right time.

Most of the step vocabulary already exists and carries over:

| Concept | Hoshin today | Hoshin.Net |
| :---- | :---- | :---- |
| Checks before a step finishes | Lifecycle events with blocking before and after hooks, read from the base branch | Command step |
| The verdict is the program's, not the agent's | `red record`, `baseline` | Red and Baseline steps |
| Retry cap at the gate | Stop `gate` with `gateRetries` | Gate step, retry limits in `Next` |
| The agent cannot push or edit protected files | `guard` (PreToolUse) | Guard, aware of the current step and active session |
| Push and pull requests | `change.done` | Git step |
| Typed contracts | Document layouts, `schema/*.json`, `Plan-Task:` trailers | Contracts in the library |
| Where a run stands | `state`, computed from git and documents | `State` |

What is new is who decides. Today the model sequences the commands. In Hoshin.Net, `Next` sequences them and the model only does the judgment work across any supported harness.

## Architecture

Every driver goes through the one `hoshin` program, and only the library reads and writes the run record. Claude Code and Antigravity talk to it through their native hooks (`Hoshin.Cli` normalizes both stdin schemas into a single internal `HookEvent`); other tools call its commands, as they call `hoshin.ts` today.

## Repositories

Hoshin.Net holds everything any host reuses: the code and every skill text. `hoshin` holds the Claude Code and Antigravity packaging of a Hoshin.Net release. A host on Microsoft Agent Framework is a separate project that references the same release.

| Repository | Holds | Ships |
| :---- | :---- | :---- |
| Hoshin.Net | `Hoshin.Workflow` (contracts, `State`, `Next`, the steps, the ledger, the Markdig readers and record schemas); `Hoshin.Cli` (the `hoshin` program: today's commands, the dual-harness hook adapter and `guard`); `skills/`, the one copy of every skill as a folder with `procedure.md` and its references, with no front matter: the role texts a subagent or agent is given (implement, review) and the skills engineers type (`build`, `accept`, ...); the tests; and the evals | The portable `hoshin` build with the `skills/` folder, and `Hoshin.Workflow` as a NuGet package with the role texts embedded |
| hoshin | Claude Code packaging (`.claude-plugin/plugin.json`, `hooks/hooks.json`, `skills/*/SKILL.md`) and Antigravity packaging (`.agents/hooks.json`, `.agents/skills/*/SKILL.md`). Each `SKILL.md` holds only YAML front matter and one line pointing at `references/procedure.md`. A release copies each Hoshin.Net skill folder, unchanged, into that skill's `references/`, and copies in the `hoshin` build | The Claude Code plugin and the Antigravity workspace/plugin bundle |
| A MAF host (separate) | Its own graph and agents, referencing the `Hoshin.Workflow` package; its agents get their instructions from the embedded role texts | Whatever it deploys |

The evals live with the skills, because a change to a skill and its eval case land in the same pull request. Hoshin.Net's CI assembles the plugin bundles from the build, the skills and `hoshin`'s manifest files to run them.

**Skills in the plugins.** No assembly. Each skill in `hoshin` (both under `.claude-plugin` and `.agents/skills/`) is a `SKILL.md` with only its YAML front matter and one line that sends the model to the procedure: "Read `references/procedure.md` and follow it." At release, the matching Hoshin.Net skill folder (`procedure.md` and its rare-path references) is copied unchanged into that skill's `references/`. Hoshin.Net stays free of YAML, and no file is rewritten on the way. The cost: the model reads the procedure with one tool call (`Read` in Claude Code, `view_file` in Antigravity) each time the skill runs, and reads a reference only when it chooses to, so the evals must show the procedure is read on every run.

The library references Markdig and nothing from Claude Code, Antigravity or MAF, so any host can sit on top of it. The contracts stay inside `Hoshin.Workflow` until a consumer needs them without the steps.

## The shared library

`Hoshin.Workflow` owns everything a run decides. The CLI calls it and never repeats its logic.

**State.** `State` is read from git, the change's documents and the ledger, as `state` is today. It holds nothing a host owns.

**Next.** One pure function returns the single step to take:

```c#
public abstract record Step;
public sealed record Implement(AgentTask Agent) : Step;
public sealed record Retry(AgentTask Agent, IReadOnlyList<string> Handoff) : Step;
public sealed record RunChecks(string Head) : Step;
public sealed record NeedsApproval(string Head, string Reason) : Step;
public sealed record Publish(string Head, string Ref) : Step;
public sealed record Done(bool Success, string Outcome) : Step;

public static class Workflow
{
    public static Step Next(State state) => /* the only place the order lives */;
}
```

**Steps that run in the library.** Each runs commands through one command runner (no shell, an allow-listed environment) and writes its verdict to the ledger:

- Task check and commits: the agent only edits the working tree. When it hands back, the library makes the task's commits itself: the new test files alone first, as `test(<scope>): <task name>` with the `Plan-Task` and `Generated-By` trailers, then everything else as `feat(<scope>): …` or `fix(<scope>): …`. It observes red on the test commit (run on a cached detached worktree) and green at HEAD.  
- Red definition for strongly-typed languages: a test commit passes the Red gate if the test runner executes and fails at least one assertion, OR if compilation of the isolated test commit fails strictly due to missing target symbols (`CS0246`, `CS0103`, `CS1061`, `TS2304`, etc.) that are resolved and pass cleanly in the subsequent Green commit at HEAD. Syntax errors in test files do not count as valid Red.  
- Test files are the files the task's `test` command names plus the repository's test patterns, which `.hoshin.json` sets (default `test/**`, `tests/**`, `*.test.*`, `*Tests.cs`).  
- A failed attempt commits nothing: its changes stay in the working tree and the retry continues from them, with the reasons as handoff.  
- Checks: the blocking `build.finish` hooks from `hooks.jsonc`, against the baseline.  
- Git: branches, commits, push and pull requests. It is the only step that writes history or sends anything off the machine.

**Agent steps.** `Implement` and `Retry` are requests, not code that runs. The library builds the brief (task text, test command, the output contract, the handoff from the last attempt) and judges what comes back from the repository alone.

**Documents.** The readers are ports of `tools/documents/` on Markdig. Every record is a fenced `jsonc` block with today's keys (`"id": "T1"`), front matter is a `jsonc` block on line 1, and `change.yml` becomes `change.jsonc`. System.Text.Json reads JSONC on its own. If an agent writes a valid bare JSONC object starting on line 1 without code fences during an authoring step, the library's commit step automatically wraps it in ```` ```jsonc ... ``` ```` fences before committing so GitHub and Markdown previews render cleanly without wasting an extra turn. A YAML front matter is refused with the way out.

**Roles.** Each role is a folder with `procedure.md` and reference files. A body says how to do the role's work, names no command, event or host, and uses harness-neutral language (e.g., "Dispatch a subagent with the brief" rather than naming `Agent` or `invoke_subagent`).

## Harness plugins: Claude Code and Antigravity

In both harnesses, the session does the agent steps and hooks connect it to the library. `Hoshin.Cli` inspects the incoming hook JSON on `stdin`, auto-detects whether the caller is Claude Code or Antigravity, normalizes the payload into a harness-neutral `HookEvent`, reads `State`, calls `Next`, runs the steps that need no agent, and injects the next step or a verdict.

| Lifecycle Boundary | Claude Code Hook | Antigravity Hook | Command | What it does |
| :---- | :---- | :---- | :---- | :---- |
| Skill / Step Start | `UserPromptExpansion` (`/hoshin:build`) and `PostToolUse` on `Skill` | `PreInvocation` (slash command) and `PostToolUse` on `view_file` (when path matches `.agents/skills/*/SKILL.md` or `IsSkillFile: true`) | `hoshin hook start` | Resolves the change, calls `Next`, injects the step's brief and output contract, writes the current step and `session_id` for the guard |
| Guard (Files & Shell) | `PreToolUse` on `Bash`, `Edit`, `Write` | `PreToolUse` on `run_command`, `write_to_file`, `replace_file_content`, `multi_replace_file_content` | `hoshin guard` | Denies what the current step does not allow: protected files, `git add`, `git commit` and other history changes, `git push` and `gh pr`. The agent edits the working tree only |
| Subagent Result (Foreground & Async) | `PostToolUse` on `Agent` | `PostToolUse` on `invoke_subagent`, `manage_task` | `hoshin hook result` | Foreground subagent: judges the attempt and injects the next brief or a retry handoff. Background/async launch: records the `agentId`/`taskId` in the ledger, tells the orchestrator to wait, refuses conflicting concurrent launches on the same worktree |
| Async Subagent Completion | `SubagentStop` | `PostToolUse` on `manage_task` (wait/output completion) or `Stop` check | `hoshin hook subagent-stop` | Judges a background subagent's attempt when that subagent finishes |
| Turn End / Gate | `Stop` | `Stop` / `PostInvocation` | `hoshin hook stop` | Lets the session stop while the step's subagent is still running in the background; otherwise blocks with the next agent step, or lets the session stop at `NeedsApproval` or `Done` |

A run from a conversation (in either Claude Code or Antigravity):

1. The engineer invokes `/hoshin:build` (or `/build` in Antigravity). `hook start` finds that `Next` returns `Implement T1` and injects its brief.  
2. The main agent dispatches a subagent (`Agent` in Claude Code, `invoke_subagent` in Antigravity) with the brief. The subagent writes the test, sees it fail, writes the code and hands back; it does not commit, and the guard denies it if it tries.  
3. When the subagent returns (either synchronously via `hook result` or asynchronously via `subagent-stop` / `manage_task`), the library commits the test, observes red, commits the code and observes green, then injects either a retry with the reasons or the next task.  
4. After the last task, the same hook runs the checks and tells the model to report that the change waits for approval.  
5. The engineer invokes `accept`. `hook start` records the approval, `Publish` pushes, and the model reports the result.

Design rules for the harness plugins:

- **Hooks live in root manifest files (`hooks/hooks.json` and `.agents/hooks.json`).** Hooks declared in a skill's front matter start working only after the skill is invoked, so they cannot catch the command that loads it, and that front matter is YAML.  
- **Chain through subagents, not Stop.** Claude Code allows at most 8 Stop blocks in a row. Chaining tasks through `PostToolUse` on subagent completion avoids hitting Stop limits in both harnesses.  
- **Track both foreground and background subagents.** Antigravity's Agent Manager and Claude Code's background subagents both support async execution. When an async launch is detected (`status: async_launched` in Claude Code or async `invoke_subagent` / `manage_task` in Antigravity), `hoshin` records the active `agentId` in the step state file and judges the working tree only when the subagent completes.  
- **The guard has a session-aware fast path.** It reads only the current-step file (which includes the active `session_id` / `conversationId` and timestamp) so an interrupted session does not lock out normal editing in a new, non-workflow session.  
- **A hook never crashes the session.** It reports what broke on stderr and exits 0\.  
- **Injected text stays under 10,000 characters.** Claude Code moves a longer value to a file and shows a 2,000-character preview.

## Intent, spec and plan

The authoring skills work through the same hooks as the build: the skill's procedure is only the judgment work (interviewing, drafting), and the library does the rest. The difference is that a person answers between turns, so the Stop hook must stay silent while a question is open. A spike confirmed it on Haiku 5.5 and Sonnet 5.5.

`Next` covers the whole change, so the order is in one place:

```
WriteIntent → AwaitAccept(intent) → WriteSpec → AwaitAccept(spec) → WritePlan → AwaitAccept(plan) → Implement T1 … → NeedsApproval → Publish
```

What moves out of the skill bodies:

| Today, in the skill body | In Hoshin.Net |
| :---- | :---- |
| `resolve`, `state` and the gate (`ready.<skill>.stop`) | `hook start` asks the library's gate; a skill typed out of order gets "the next step is …" to pass on |
| `doc.accept` on the upstream document | `hook start`, before the work begins |
| The staleness check of context files | `hook start` names the context files and which are behind |
| Finding the change, its branch and copy | `hook start`, from the command's arguments |
| Source pins | Written by the library when it commits |
| `commit --kind intent-draft`, `spec`, `plan`, with the format check | The library, at the Stop hook |
| "Change `status` to `accepted` and commit" | `accept`, which the library records and commits |

What stays in the procedures is judgment: the interview guide and triage of edge cases (intent), checking requirements against the policy skills and naming owners for concerns (spec), and the task-drafting rules (plan).

During a writing step, the Stop hook decides by the document alone:

1. **Unchanged:** The model asked a question or is waiting. The turn ends silently.  
2. **Changed, with problems:** the hook blocks with the list. The model fixes the file, or asks its next question if it is still interviewing.  
3. **Changed and clean:** the library auto-wraps bare line-1 JSONC front matter in ```` ```jsonc ```` fences if needed, commits it with the change's scope and trailer, and tells the model to say it is ready for review and that `accept` accepts it.

While a document is written or awaits acceptance, the guard lets only that document change and refuses commits by hand.

**The spec's check.** At Stop the library reads the spec's requirements block with Cucumber's own parser for .NET (the `Gherkin` NuGet package) and applies hoshin's spec rules: one `@R<n>` and at least one `@from:` per rule, one `@R<n>.<m>` per scenario matching its rule, IDs used once, a scenario under every rule, every outline parameter in its examples, and no `@R` tag outside the block. When the spec is clean it writes `source_intent_commit` and commits. hoshin's nine spec conformance examples, converted to JSONC front matter, read exactly as the TypeScript program's results.

## Running without a person

There is no separate deployed host. A run without a person uses the same plugin, headless: `claude -p "/hoshin:build" --plugin-dir …` or `agy` in headless/CI mode in a disposable container or CI runner, then the approval as a second session. The hooks, the guard and the verdicts are the same whoever starts the run.

Tools that read or gate a change, such as the Kata Workbench and CI checks, call `hoshin` commands and read their JSON, as they call `hoshin.ts` today.

## Run state

A run's state lives where hoshin keeps it today, so a stopped session, a crash or a second session picks up exactly where the last one left off:

1. **Everything `Next` reads lives in git, the documents or the ledger.** Attempts handed out, active async `agentId`s, verdicts, check results, approvals and pushes are ledger entries.  
2. **One small file beside the ledger holds the current step and active `session_id` / `conversationId`,** so the guard never computes `State`. It is rewritten at every step and can always be rebuilt from the ledger.  
3. **`Next` returns one step at a time.**  
4. **One suite tests `Next`:** given this state, expect this step. The hooks are tested by feeding `hoshin` both Claude Code and Antigravity JSON payloads, with a scripted agent doing the task work.  
5. **No hook code inside the library.** Code that knows about Claude Code or Antigravity belongs in `Hoshin.Cli`.

The ledger stays in the master repository's git folder, local to the machine that ran the step, as today.

## Distribution and compatibility

**The plugin's program.** `hoshin` ships as one portable, framework-dependent build: `hoshin.dll`, `Hoshin.Workflow.dll` and `Markdig.dll`, 860 KB, the same files on every OS and CPU. Hoshin.Net's CI builds it at each release, and the `hoshin` repository commits that build, with the release's skills, into both the Claude Code and Antigravity plugin structures. Every hook runs it as `dotnet "<plugin-lib>/hoshin.dll" …`. The executable that runs is Microsoft's signed `dotnet` host, and the plugin carries no native executable, so code signing, Gatekeeper and SmartScreen do not come into it. Engineers need the .NET 10 runtime; `RollForward` set to `Major` lets a later runtime run it too.

**What it costs.** Each hook starts the .NET runtime: about 155 ms a call on Linux (153 to 165 ms measured; `dotnet --version` alone takes as long). Native AOT took 10 to 40 ms, and today's `node hoshin.ts guard` 290 to 330 ms. A task's subagent made about 15 guard calls in the spike, so about 2 seconds a task. If Windows Defender / EDR cold-start overhead ever matters on managed Windows machines, per-platform Native AOT binaries remain a fallback.

**Clients.** `tools/CLIENTS.md` makes `hoshin` part of the contract with the Kata Workbench. Since JSONC front matter is already a breaking change for Kata (which reads and writes `status`, `scope`, and `source_commit` directly), Hoshin.Net ships as a major version cut alongside an updated Kata front-matter reader.

## Spike results

Four spikes ran on 2026-11-15 on a three-task toy change in the `hoshin-net` capsule of the experiments repository (`feat/hoshin-net-spikes`), and a fifth spike validates the Antigravity hook surface.

| Spike | Result |
| :---- | :---- |
| Plan reader on Markdig with JSONC records | hoshin's 11 layout-2 plan examples, converted line for line, read exactly as the TypeScript program's expected results, in tests and through `hoshin read --json` |
| Hooks alone drive a build | Haiku 5.5 built the three tasks in 147 s for \$0.04, Sonnet 5.5 in 100 s for \$0.55. Every task's test was committed first and observed failing; `/hoshin-net:accept` in a second session published the branch. No skill body names the program |
| Departures are caught | During subagent execution, the guard denied production code edits until the test file was modified and observed failing. At `PostToolUse`, the library committed the test file first, observed Red, and committed the implementation. The verdict refused 3 attempts whose `Plan-Task` trailer git could not read, and each retry passed |
| Guard start-up | 10 to 40 ms as Native AOT, about 155 ms as a portable build under `dotnet`, 290 to 330 ms for today's `node hoshin.ts guard` |
| Spike 5: Antigravity hook & subagent probe | Verifies `.agents/hooks.json` payload normalization across `PreInvocation`, `PreToolUse` (`run_command`, `write_to_file`, `replace_file_content`), `PostToolUse` (`invoke_subagent`, `manage_task`), and `Stop` in Antigravity on Windows |

## Rollout

Hoshin.Net is developed using Antigravity, and Rollout 1 targets both Antigravity and Claude Code. Each phase ships on its own, so the work can stop after any of them.

1. **Spikes (1–4 Done; Spike 5 in Antigravity).** Run the Antigravity hook probe (`Spike 5`) on Windows to lock in the `IHookAdapter` payload schemas for `agy` alongside Claude Code.  
2. **Library, readers and command line.** Port the spec, build-log and review readers and every command to `Hoshin.Workflow` and `hoshin`, with JSONC records. Exit when the existing black-box tests, with JSONC fixtures, pass against `hoshin` on Linux, macOS and Windows.  
3. **Document migration.** Extend `hoshin migrate` to rewrite a repository's YAML documents (both Layout 1 and Layout 2\) and `change.yml` as Layout 2 JSONC, line for line. Exit when a real intent repository migrates and `hoshin check` reports no problems.  
4. **`Next`, dual-harness hooks, and the build skill.** Add `State`, `Next`, and the dual-harness `hook` / `guard` commands (`ClaudeCode` \+ `Antigravity` payload normalizers), and rewrite the build skill with no mention of the program or harness tool names. Exit when the build eval cases pass on both Claude Code and Antigravity.  
5. **Intent, spec, plan, fix, review and done.** Move the remaining skills the same way. Exit when their eval cases pass across both harnesses and no skill body names a command.  
6. **Retire the TypeScript program.** Cut a major release of `hoshin` once Kata calls `hoshin` and reads JSONC front matter.

## Resolved design decisions & remaining open items

- [x] **Front matter format:** Canonical format is a fenced ```` ```jsonc ```` block on line 1; if a model writes a bare JSON object starting on line 1, the library auto-wraps it in ```` ```jsonc ```` fences on commit.  
- [x] **Kata Workbench compatibility:** Major version cut alongside JSONC front-matter support in Kata.  
- [x] **Layout 1 documents:** Migrate Layout 1 documents to Layout 2 JSONC via `hoshin migrate` and omit the legacy Layout 1 reader from `Hoshin.Workflow`.  
- [ ] **Antigravity Spike 5 verification:** Confirm `hooks.json` behavior across the Antigravity CLI (`agy`) and IDE Agent Manager surfaces on Windows, and capture exact `invoke_subagent` / `manage_task` async completion payloads.  
- [ ] **Skill tool payload inspection:** Capture exact `tool_input` / `toolCall.args` payloads for model-invoked skills in both Claude Code (`Skill`) and Antigravity (`view_file` on `SKILL.md`).