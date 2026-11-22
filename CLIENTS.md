# What a tool that drives hoshin depends on (v2.0.0 — Hoshin.Net)

The program has two kinds of caller: the plugin's own hooks/skills (Claude Code and Google Antigravity), and tools that run it as a subprocess and read what it prints. This file lists what external clients depend on so breaking changes are visible in review.

The primary external client is the Kata Workbench (`crates/kata-sdlc` in `satish-krishna/kata`). See [docs/KATA_MIGRATION.md](docs/KATA_MIGRATION.md) for the v2.0.0 breaking-change migration guide when switching from `node tools/hoshin.ts` to `dotnet hoshin.dll`.

## The rule

Everything listed here is the contract. A pull request that removes or renames a listed command, option, field or value, changes a listed field's type, makes a listed field absent where it was present, or rewords a listed message is a breaking change: its title carries `!`, the release is a major version, and `CHANGELOG.md` names what a client must change.

Adding is not breaking. A client ignores a field, a value of `type` in a stream, or a command it does not know. Two additions need a note in `CHANGELOG.md` all the same, because a client may not ignore them:

- A new value of a field whose values are listed below. Kata refuses a `layout`, a `stale` `reason`, an `instructions` state and a `checks` `state` it does not know.
- A new required field in `Documents/Schemas/read-result.schema.json`. A client that validates the program's output against its own copy of the schema needs the new copy.

`tests/Hoshin.Tests/BlackBox/clients.test.mjs` checks this file against the program: every command and option below is in the usage text, and every field, value and message below still appears in the program's source or schemas.

## What every call relies on

- The call is `dotnet <plugin>/lib/hoshin.dll <command> ...` (requiring .NET 10+ runtime with `RollForward=Major`), with nothing on standard input and no environment variable set for the program.
- Fields: `problems`, `outcome`, `reason`, `failed.id`, `failed.onFailure`, `failed.output`, `repo`, `repos`
- Values: `passed`, `failed`, `could-not-run`
- The answer is the last non-empty line of standard output, one JSON object. `--stream` prints other lines before it.
- Exit 0 is an answer. Exit 1 is an answer that refuses or reports problems: `problems`, a list of strings, says why, else `reason`. Exit 2 means the program could not run, and the reason is `problems` when the last line is JSON, else standard error.
- For an event (`run`, `change start`), exit 1 with `outcome` `failed` is a hook that failed, named by `failed`; exit 1 with any other outcome is a refusal.
- Options: `--json`, `--master`, `--workspace`

## `resolve`

- Options: `--json`, `--master`, `--workspace`
- Fields: `master`, `workspace`, `layout`, `problems`
- Values: `workspace`, `single-repo`
- An empty `master` or `workspace` means the folder is not placed, and `problems` says why.

## `state`

- Options: `--json`, `--system`, `--change`, `--open`, `--master`, `--workspace`
- Fields: `system`, `changes`, `problems`, `change`, `master`, `folder`, `inCopy`, `title`, `track`, `documents.intent`, `documents.spec`, `documents.plan`, `exists`, `inHead`, `uncommitted`, `headStatus`, `workingStatus`, `pin`, `scenarios`, `tasks`, `all`, `logged`, `unlogged`, `commits`, `untasked`, `buildLogs`, `review.committed`, `review.draft`, `fix.row`, `fix.facts`, `unrecorded`, `ownLinesOnly`, `accepted`, `acceptanceLast`, `built`, `ready.spec`, `ready.plan`, `ready.build`, `ready.review`, `ready.done`, `ready.reject`, `candidate`, `stop`, `accept`, `finished`, `apps`, `touched`, `head`
- Values: `full`, `fix`, `accepted`, `superseded`, `rejected`, `missing`, `broken`, `holds`, `clean`, `added-entries`, `other`, `none`, `next-section`, `leading`, `merged`
- A null `tasks` is read as a plan that cannot be read. A change is built when every repository's `unlogged` is empty.
- A non-null `ready.<skill>.stop` is shown to the engineer as it is, and disables the action.

## `list`

- Options: `--json`, `--system`, `--master`, `--workspace`
- Fields: `system`, `changes`, `problems`, `change`, `title`, `track`, `master`, `inCopy`, `finished`
- Values: `full`, `fix`, `merged`, `rejected`

## `stale`

- Options: `--json`, `--system`, `--master`, `--workspace`
- Fields: `repos`, `repo`, `path`, `reason`, `behind`
- Values: `current`, `behind`, `missing`, `no-source-commit`, `unknown-commit`

## `instructions`

- Options: `--json`, `--master`, `--workspace`
- Fields: `workspaceMd`, `claudeMd`
- Values: `missing`, `stale`, `bad-markers`, `current`, `home-folder`

## `hooks`

- Options: `--json`, `--system`, `--event`, `--master`, `--workspace`
- Fields: `hooks`, `problems`, `id`, `event`, `when`, `blocking`, `onFailure`, `repos`, `apps`, `timeout`
- Values: `fix`, `stop`, `build.finish`

## `status`

- Options: `--json`, `--change`, `--master`, `--workspace`
- Fields: `repos`, `repo`, `head`, `runs`, `event`, `key`, `outcome`, `hooks`, `problems`, `failed`, `current`, `at`, `details.untouched`, `id`, `output`, `onFailure`
- Values: `build.finish`, `change.done`, `passed`, `failed`, `could-not-run`, `fix`, `stop`

## `run`

- Options: `--json`, `--system`, `--change`, `--repo`, `--document`, `--check`, `--stream`, `--master`, `--workspace`
- Events: `doc.accept` with `--document`, `build.start` with `--repo` (several, with `--check`), `change.done` with `--stream`.
- Fields: `details.uncommitted`, `details.pullRequests`, `details.untouched`, `details.pushed`, `url`, `number`, `repo`

## `change start`

- Options: `--json`, `--system`, `--change`, `--type`, `--scope`, `--title`, `--branch`, `--master`, `--workspace`
- Fields: `details.branch`, `details.copy`, `event`, `system`, `outcome`, `change`

## `change show`

- Options: `--json`, `--change`, `--refresh`, `--master`, `--workspace`
- Fields: `change`, `system`, `branch`, `record`, `type`, `scope`, `base`, `pullRequests`, `url`, `number`, `pushed`, `repos`, `path`, `checkedOut`, `states`
- Values: `OPEN`, `MERGED`, `CLOSED`, `master`

## `change rename`

- Options: `--json`, `--system`, `--change`, `--to`, `--master`, `--workspace`
- Fields: `from`, `to`, `renamed`

## `change remove`

- Options: `--json`, `--change`, `--abandon`, `--master`, `--workspace`
- Fields: `kept`

## `commit`

- Options: `--json`, `--kind`, `--system`, `--change`, `--master`, `--workspace`
- Values: `accept-intent`, `accept-spec`, `accept-plan`, `reject-intent`, `reject-fix`
- Fields: `problems`, `reason`

## `read`

- Options: `--json`
- Fields: `kind`, `layout`, `document`, `problems`, `file`, `line`, `message`, `requirements`, `id`, `title`, `from`, `scenarios`, `outline`, `steps`, `examples`, `tasks`, `shared_decisions`, `name`, `repo`, `depends_on`, `uses`, `files`, `covers`, `supports`, `sections`, `body`, `text`, `question`, `owner`, `function`, `entries`, `task`, `commits`, `departure`, `reviews`, `reviewed_at`, `findings`, `severity`, `concern`, `rule`, `test`, `compile`, `red`
- Values: `spec`, `plan`, `build-log`, `review`, `high`, `medium`, `low`, `bug`, `convention`
- Hoshin.Net v2.0.0 reads **layout 2 JSONC** only; use `hoshin migrate` to upgrade legacy YAML and layout 1 documents.

## `red show`

- Options: `--json`, `--change`, `--repo`, `--task`, `--master`, `--workspace`
- Fields: `task`, `observed`, `reason`, `record`, `record.output`, `record.commit`, `record.outcome`, `problems`
- Values: `failed`, `passed`, `could-not-compile`, `could-not-run`

## `migrate`

- Options: `--json`, `--write`, `--dry-run`, `--documents`, `--master`, `--workspace`
- Fields: `workspace`, `master`, `layout`, `write`, `steps`, `step`, `path`, `to`, `status`, `detail`, `kept`, `manual`, `problems`
- Values: `marker`, `copy`, `state`, `instructions`, `done`, `would`, `current`, `manual`, `refused`

## `checks`

- Options: `--json`, `--change`, `--repo`, `--system`, `--master`, `--workspace`
- Fields: `system`, `change`, `runs`, `run`, `event`, `repo`, `phase`, `checks`, `check`, `state`, `startedAt`, `endedAt`, `timeout`, `outcome`, `ids`, `blocks`, `problems`
- Values: `ended`, `running`, `abandoned`, `baseline`, `head`, `passed`, `failed`, `could-not-run`

## `baseline show`

- Options: `--json`, `--change`, `--repo`, `--system`, `--master`, `--workspace`
- Fields: `base`, `taken`, `reused`, `hooks`, `id`, `outcome`, `exitCode`, `ids`, `output`, `needsApproval`, `approved`, `problems`
- Values: `passed`, `failed`

## `baseline approve`

- Options: `--json`, `--change`, `--repo`, `--hook`, `--system`, `--master`, `--workspace`
- Fields: `approved`, `hook`, `base`, `problems`

## `check`

- Each line of standard output is `<file>:<line>: <message>`, with `<file>` the relative path that was given.

## `naming`

- Options: `--json`, `--type`, `--scope`, `--change`, `--title`
- Fields: `declared`, `setting`, `branch`, `pullRequestTitle`, `problems`, `names`

## `scope`

- Options: `--json`
- Fields: `problems`

## Message text

- `no change ` followed by the change, then ` in system ` and the system
- `the scope: `
- `the team naming on the base branch: `
- ` is taken in ` and ` clashes in `
- `names starting "change/pending-" are reserved`
- `a branch name cannot`
- `not a document this program knows`
- `not observed: `
- `No master repository found`

## Files a client reads itself

- `.hoshin.json` in the master repository's root
- `.ai-sdlc.json` in the master repository's root (for legacy migration detection)
- `<workspace>/.hoshin/copies/<change>`
- `intent/<system>/<change>/change.jsonc` (migrated from `change.yml` in v2.0.0)
- `intent/<system>/<change>/intent.md`, `spec.md`, and `plan.md`: line-1 fenced ```` ```jsonc ```` front matter (`status`, `scope`, `source_intent_commit`, `source_spec_commit`)
- `intent/<system>/context/<repo>.md`: line-1 fenced ```` ```jsonc ```` front matter (`source_commit`)
- `intent/workspace.md` and workspace `CLAUDE.md`: block between `<!-- hoshin:start -->` and `<!-- hoshin:end -->`
- `.claude-plugin/plugin.json` (Claude Code) and `.agents/hooks.json` (Google Antigravity)
- `skills/build/SKILL.md`
