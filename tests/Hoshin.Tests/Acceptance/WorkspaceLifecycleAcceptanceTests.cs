using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Tests.Unit;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Acceptance;

/// <summary>
/// R0 acceptance gate (docs/review/ANALYSIS.md §12.2). Written by the reviewer, not the implementer; the implementer
/// must not edit this file to make it pass. Changes to it go through the reviewer.
///
/// It drives the shipped CLI (<c>dotnet hoshin.dll</c>) with Claude Code hook payloads, exactly as the harness does,
/// through one whole change in a realistic workspace, and judges only what an engineer can see: commits, branches,
/// remote refs, committed documents, pull requests, and whether the session is allowed to end. It never reads
/// <c>.git/hoshin</c>, so it does not care how the fix is designed.
///
/// The user path is the original hoshin plugin's (D:\workspaces\hoshin):
/// - the master repository is not named <c>master</c> (here <c>ai-sdlc</c>, as in a real workspace);
/// - intent, spec and plan are written and accepted from a session in the master repository;
/// - each repository is built from a session started inside it (hoshin <c>skills/build/SKILL.md</c> lines 25 and 28),
///   so that repository's own CLAUDE.md, skills and hooks are loaded; a session never builds another repository's tasks;
/// - review findings are fixed by a build run from inside the repository, with the engineer's instructions
///   (hoshin <c>skills/review/SKILL.md</c> line 111), and then reviewed again;
/// - only the engineer approves, and approving publishes: every touched repository is pushed and gets a pull
///   request, the master's last; after that the change stays done.
///
/// The plan has one task whose test runs against compiled output (<c>compile</c>, output gitignored) and one whose test
/// runs a gitignored tool (<c>node_modules</c>), because real repositories look like that.
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class WorkspaceLifecycleAcceptanceTests
{
    private const string MasterName = "ai-sdlc";
    private const string SystemName = "shop";
    private const string ChangeSlug = "add-shout";
    private const int MaxHookRoundsPerTurn = 12;

    [Fact]
    public void WholeChange_FromIntentToMergedReadyPullRequests_InAWorkspaceWhoseMasterIsNotNamedMaster()
    {
        using var ws = ShopWorkspace.Create();
        var po = ws.Session("po-session", ws.Master);

        // 1. Authoring, from the master repository, by the product owner and the engineer.
        var plan = ws.AuthorAndAcceptAll(po);
        BuildReviewFixAndPublish(ws, po, plan);
    }

    /// <summary>Stages 2 to 9: from an accepted plan to published pull requests, and done stays done.</summary>
    private static void BuildReviewFixAndPublish(ShopWorkspace ws, Session po, DocumentRef plan)
    {
        var changeFolder = plan.Folder;

        // 2. Build api from a session started inside api: T1 only (its test needs `compile` output, gitignored).
        var apiDev = ws.Session("api-session", ws.Api);
        apiDev.Turn(apiDev.Prompt("build"));
        ws.AssertTaskCommitted(ws.Api, "T1", apiDev);
        ws.AssertNotCommitted(ws.Api, "dist/", apiDev);

        // 3. Build web from a session started inside web: T2 only (its test runs a gitignored tool).
        var webDev = ws.Session("web-session", ws.Web);
        webDev.Turn(webDev.Prompt("build"));
        ws.AssertTaskCommitted(ws.Web, "T2", webDev);
        ws.AssertNotCommitted(ws.Web, "node_modules/", webDev);

        // Both tasks are logged in the change folder, which is how every later step knows they are done.
        ws.AssertBuildLogHas(changeFolder, "api", "T1", webDev);
        ws.AssertBuildLogHas(changeFolder, "web", "T2", webDev);

        // 4. Review 1, from the master repository unless the build already asked for it. It finds one high bug in web.
        if (!ws.ReviewCommitted(changeFolder, 1))
        {
            po.Turn(po.Prompt("review"));
        }

        Assert.True(ws.ReviewCommitted(changeFolder, 1), po.Transcript("review 1 must be written and committed"));

        // 5. The high finding is fixed by a build run from inside web, with the engineer's instructions.
        ws.FixRequested = true;
        var headBeforeFix = ws.Head(ws.Web);
        webDev.Turn(webDev.Prompt("build", "fix F1 from review 1: shout must trim the name before shouting"));
        Assert.True(ws.FixApplied, webDev.Transcript("a build run in web with fix instructions must dispatch the fix"));
        Assert.NotEqual(headBeforeFix, ws.Head(ws.Web));
        Assert.Contains("shout-trim.test.mjs", ws.Git(ws.Web, "log", "--name-only", "--format=", $"{headBeforeFix}..HEAD"));

        // 6. Review 2 of the fixed change, from the master repository unless the fix run already asked for it. Clean.
        if (!ws.ReviewCommitted(changeFolder, 2))
        {
            po.Turn(po.Prompt("review"));
        }

        Assert.True(ws.ReviewCommitted(changeFolder, 2), po.Transcript("review 2 must be written and committed after the fix"));

        // 7. Approval is the engineer's: a model-typed accept must not publish anything.
        po.Turn(po.ModelInvokedSkill("accept"));
        Assert.Empty(ws.PullRequests());
        Assert.False(ws.RemoteHasChangeBranch(ws.Web), po.Transcript("a model-invoked accept must not publish"));

        // 8. The engineer approves; approving publishes.
        po.Turn(po.Prompt("accept"));
        var prs = ws.PullRequests();
        Assert.True(prs.Count == 3, po.Transcript($"expected one pull request per touched repository (api, web, master), got {prs.Count}: {string.Join(", ", prs.Select(p => p.Repo))}"));
        Assert.Equal(["api", "web"], prs.Take(2).Select(p => p.Repo).Order(StringComparer.Ordinal));
        Assert.NotEqual("api", prs[2].Repo);
        Assert.NotEqual("web", prs[2].Repo);
        Assert.Equal(ws.Head(ws.Api), ws.RemoteHead(ws.Api));
        Assert.Equal(ws.Head(ws.Web), ws.RemoteHead(ws.Web));
        var masterCopy = plan.Root;
        Assert.Equal(ws.Head(masterCopy), ws.RemoteHead(masterCopy));

        // 9. Done stays done: nothing re-runs checks, review or approval, nothing is published again, and no session is held.
        var headsAfterPublish = (Api: ws.Head(ws.Api), Web: ws.Head(ws.Web), Master: ws.Head(masterCopy));
        po.AssertTurnEndsQuietly("after publishing, the master session must be allowed to end");
        webDev.AssertTurnEndsQuietly("after publishing, the web session must be allowed to end");
        apiDev.AssertTurnEndsQuietly("after publishing, the api session must be allowed to end");
        webDev.Turn(webDev.Prompt("build"));
        Assert.Equal(headsAfterPublish, (ws.Head(ws.Api), ws.Head(ws.Web), ws.Head(masterCopy)));
        Assert.Equal(3, ws.PullRequests().Count);
        Assert.False(ws.ReviewCommitted(changeFolder, 3), webDev.Transcript("a published change must not ask for another review"));
    }

    [Fact]
    public void InterruptedBuild_IsResumedByTheEngineer_AndNeverTrapsAnotherSession()
    {
        using var ws = ShopWorkspace.Create();
        var po = ws.Session("po-session", ws.Master);
        ws.AuthorAndAcceptAll(po);

        // The engineer starts the web build, then the model ends its turn again and again without dispatching.
        var first = ws.Session("web-first", ws.Web);
        var brief = first.Context(first.Hook("hook start", first.PromptPayload("build", "")));
        Assert.True(brief is not null && brief.Contains(ShopWorkspace.BeginMarker, StringComparison.Ordinal), first.Transcript("/hoshin:build in web must dispatch T2"));
        for (var i = 0; i < 6; i++)
        {
            first.Hook("hook stop", first.StopPayload(stopHookActive: i > 0));
        }

        // Another session in the same repository, unrelated to the build, must be able to end its turn.
        var other = ws.Session("web-unrelated", ws.Web);
        other.AssertTurnEndsQuietly("an unrelated session must never be blocked by a parked build");

        // The engineer comes back in a new session and resumes: T2 is dispatched again and completes.
        var resumed = ws.Session("web-resumed", ws.Web);
        resumed.Turn(resumed.Prompt("build"));
        ws.AssertTaskCommitted(ws.Web, "T2", resumed);
        resumed.AssertTurnEndsQuietly("after T2, the resumed web session must be allowed to end");
    }

    [Fact]
    public void ActiveBuild_DeniesPushesThroughEveryShellTool_TheHarnessOffers()
    {
        using var ws = ShopWorkspace.Create();
        var po = ws.Session("po-session", ws.Master);
        ws.AuthorAndAcceptAll(po);

        var webDev = ws.Session("web-session", ws.Web);
        var brief = webDev.Context(webDev.Hook("hook start", webDev.PromptPayload("build", "")));
        Assert.True(brief is not null && brief.Contains(ShopWorkspace.BeginMarker, StringComparison.Ordinal), webDev.Transcript("/hoshin:build in web must dispatch T2"));

        // Claude Code on Windows offers PowerShell as a shell tool next to Bash; both must be guarded.
        foreach (var tool in new[] { "Bash", "PowerShell" })
        {
            var decision = ShopWorkspace.PermissionDecision(webDev.Hook("guard", webDev.ToolPayload(tool, new { command = "git push origin HEAD:refs/heads/main" })));
            Assert.True(decision == "deny", webDev.Transcript($"`git push` through the {tool} tool during an active build must be denied, got `{decision ?? "allow"}`"));
        }

        // And the shipped hook configuration must route those tools to the guard at all.
        var hooksJson = File.ReadAllText(Path.Combine(ShopWorkspace.RepoRoot, "packaging", "claude-code", "hooks", "hooks.json"));
        var matcher = JsonDocument.Parse(hooksJson).RootElement.GetProperty("hooks").GetProperty("PreToolUse")
            .EnumerateArray().Select(e => e.GetProperty("matcher").GetString() ?? "").Single(m => m.Contains("Bash", StringComparison.Ordinal));
        Assert.Contains("PowerShell", matcher.Split('|'));
    }

    /// <summary>A real workspace: <c>ai-sdlc</c> (master), <c>api</c> and <c>web</c>, each with a bare remote outside the workspace.</summary>
    private sealed class ShopWorkspace : IDisposable
    {
        public const string BeginMarker = "<<<BEGIN SUBAGENT PROMPT";
        public const string EndMarker = "END SUBAGENT PROMPT>>>";

        public static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        private static readonly string HoshinDll = Path.Combine(AppContext.BaseDirectory, "hoshin.dll");
        private static readonly string RolesDir = Path.Combine(RepoRoot, "skills");

        private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

        private readonly TempDir _temp;

        private ShopWorkspace(TempDir temp)
        {
            _temp = temp;
            Root = Path.Combine(temp.Path, "ws");
            Remotes = Path.Combine(temp.Path, "remotes");
            Master = Path.Combine(Root, MasterName);
            Api = Path.Combine(Root, "api");
            Web = Path.Combine(Root, "web");
            GhDir = Path.Combine(temp.Path, "gh");
        }

        public string Root { get; }
        public string Remotes { get; }
        public string Master { get; }
        public string Api { get; }
        public string Web { get; }
        public string GhDir { get; }
        public bool FixRequested { get; set; }
        public bool FixApplied { get; private set; }

        public static ShopWorkspace Create()
        {
            var ws = new ShopWorkspace(new TempDir("hoshin-r0-"));
            Directory.CreateDirectory(ws.Root);
            Directory.CreateDirectory(ws.Remotes);
            ws.InitRepo(ws.Master, new Dictionary<string, string>
            {
                [".hoshin.json"] = JsonSerializer.Serialize(new
                {
                    version = 1,
                    systems = new Dictionary<string, object> { [SystemName] = new { repos = new[] { "api", "web" } } },
                    hooks = new Dictionary<string, object> { ["build.finish"] = new[] { new { id = "unit", run = new[] { "node", "--test" } } } },
                }, IndentedJson) + "\n",
                [$"intent/{SystemName}/policies.md"] = "# Policies\n\nNone yet.\n",
            });
            ws.InitRepo(ws.Api, new Dictionary<string, string>
            {
                ["package.json"] = "{ \"name\": \"api\", \"type\": \"module\", \"private\": true }\n",
                [".gitignore"] = "dist/\n",
                ["build.mjs"] = """
                    import { cpSync, mkdirSync } from 'node:fs';
                    mkdirSync('dist', { recursive: true });
                    cpSync('src', 'dist', { recursive: true });
                    """,
                ["src/calc.mjs"] = "export const add = (a, b) => a + b;\n",
                ["test/add.test.mjs"] = """
                    import test from 'node:test';
                    import assert from 'node:assert/strict';
                    import { add } from '../dist/calc.mjs';
                    test('add', () => { assert.equal(add(2, 3), 5); });
                    """,
            });
            ws.RunNode(ws.Api, "build.mjs"); // The engineer has built api once, as anyone working in it has.
            ws.InitRepo(ws.Web, new Dictionary<string, string>
            {
                ["package.json"] = "{ \"name\": \"web\", \"type\": \"module\", \"private\": true }\n",
                [".gitignore"] = "node_modules/\n",
                ["src/greet.mjs"] = "export const greet = (name) => `Hello, ${name}`;\n",
                ["test/greet.test.mjs"] = """
                    import test from 'node:test';
                    import assert from 'node:assert/strict';
                    import { greet } from '../src/greet.mjs';
                    test('greet', () => { assert.equal(greet('Ada'), 'Hello, Ada'); });
                    """,
            });
            // An installed dev tool, gitignored, as `npm install` leaves it.
            TestGitFixture.Write(Path.Combine(ws.Web, "node_modules", "testkit", "run.mjs"), """
                import { spawnSync } from 'node:child_process';
                const r = spawnSync(process.execPath, ['--test', ...process.argv.slice(2)], { stdio: 'inherit' });
                process.exit(r.status ?? 1);
                """);
            Directory.CreateDirectory(ws.GhDir);
            File.WriteAllText(Path.Combine(ws.GhDir, "gh.cjs"), FakeGh);
            return ws;
        }

        public Session Session(string id, string cwd) => new(this, id, cwd);

        /// <summary>Intent, spec and plan written and accepted from the master repository; returns the plan.</summary>
        public DocumentRef AuthorAndAcceptAll(Session po)
        {
            DocumentRef? doc = null;
            foreach (var skill in new[] { "intent", "spec", "plan" })
            {
                po.Turn(skill == "intent" ? po.Prompt("intent", ChangeSlug) : po.Prompt(skill));
                doc = po.LastDocument;
                Assert.True(doc?.FileName == $"{skill}.md", po.Transcript($"/hoshin:{skill} must ask for {skill}.md to be written"));
                AssertCommitted(doc!, po.Transcript($"the {skill} draft is committed when the turn ends"));
                po.Turn(po.Prompt("accept"));
                AssertHeadStatus(doc!, "accepted", po.Transcript($"the engineer's /hoshin:accept accepts the {skill}"));
            }

            return doc!;
        }

        // ---------------------------------------------------------------- the compliant model

        /// <summary>Writes the document a brief asks for, as a compliant model would.</summary>
        public void WriteDocument(DocumentRef doc, string brief)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string FrontMatter(string startsWith) =>
                (brief.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(startsWith, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"the brief gives no front matter starting with {startsWith}:\n{brief}"))
                .Replace("<short title>", "Shout greeting", StringComparison.Ordinal)
                .Replace("<intent title>", "Shout greeting", StringComparison.Ordinal)
                .Replace("<name and role>", "Pat, product owner", StringComparison.Ordinal)
                .Replace("<YYYY-MM-DD>", today, StringComparison.Ordinal);

            var text = doc.FileName switch
            {
                "intent.md" => $$"""
                    ```jsonc
                    {{FrontMatter("{ \"title\"")}}
                    ```

                    # Intent: Shout greeting

                    ## Problem

                    Callers cannot emphasize a greeting, and the calculator cannot multiply.

                    ## Proposed outcome

                    The web greeting can shout, and the api calculator can multiply.

                    ## Affected users and systems

                    Web visitors and api clients.

                    ## Constraints

                    Existing greetings and sums are unchanged.

                    ## Decisions

                    D1: the api calculator multiplies two numbers.
                    D2: a shouted greeting ends with `!`.

                    ## Open questions

                    None.
                    """,
                "spec.md" => $$"""
                    ```jsonc
                    {{FrontMatter("{ \"layout\": 2, \"title\"")}}
                    ```

                    # Spec: Shout greeting

                    ## Requirements

                    ```gherkin
                    Feature: Shout greeting
                      @R1 @from:D1
                      Rule: The calculator multiplies
                        @R1.1
                        Scenario: Multiplying three by four
                          Given the numbers 3 and 4
                          When they are multiplied
                          Then the result is 12

                      @R2 @from:D2
                      Rule: A shouted greeting ends with an exclamation mark
                        @R2.1
                        Scenario: Shouting at Ada
                          Given the name "Ada"
                          When the greeting is shouted
                          Then the result is "Hello, Ada!"
                    ```
                    """,
                "plan.md" => $$"""
                    ```jsonc
                    {{FrontMatter("{ \"layout\": 2, \"system\"")}}
                    ```

                    # Plan: Shout greeting

                    ## Tasks

                    ### T1: Multiply in the calculator

                    ```jsonc
                    {
                      "id": "T1",
                      "repo": "api",
                      "depends_on": [],
                      "covers": ["R1.1"],
                      "compile": ["node", "build.mjs"],
                      "test": ["node", "--test", "test/multiply.test.mjs"]
                    }
                    ```

                    #### Change

                    Add `multiply(a, b)` to `src/calc.mjs`.

                    #### Tests

                    `test/multiply.test.mjs` checks `multiply(3, 4) === 12` against the built `dist/calc.mjs`.

                    ### T2: Shout the greeting

                    ```jsonc
                    {
                      "id": "T2",
                      "repo": "web",
                      "depends_on": [],
                      "covers": ["R2.1"],
                      "test": ["node", "node_modules/testkit/run.mjs", "test/shout.test.mjs"]
                    }
                    ```

                    #### Change

                    Add `shout(name)` to `src/greet.mjs`.

                    #### Tests

                    `test/shout.test.mjs` checks `shout('Ada') === 'Hello, Ada!'`.
                    """,
                "review.md" => ReviewText(doc, brief),
                _ => throw new InvalidOperationException($"the compliant model does not know how to write {doc.FileName}"),
            };
            TestGitFixture.Write(doc.FullPath, text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        }

        private string ReviewText(DocumentRef doc, string brief)
        {
            var number = int.Parse(Regex.Match(brief, @"\(Review (\d+)\)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var header = $$"""
                ## Review {{number}}

                ```jsonc
                { "review": {{number}}, "reviewed_at": "{{DateTime.UtcNow:yyyy-MM-dd}}", "commits": { "api": "{{Head(Api)[..7]}}", "web": "{{Head(Web)[..7]}}" } }
                ```
                """;
            var body = number == 1
                ? """

                    ### F1

                    ```jsonc
                    { "id": "F1", "severity": "high", "repo": "web", "concern": "bug", "file": "src/greet.mjs", "line": 2 }
                    ```

                    `shout('  Ada ')` returns `Hello,   Ada !`: the name is not trimmed, so padded input from the form shouts garbage.
                    """
                : "\nNo findings.\n";
            var existing = File.Exists(doc.FullPath) ? File.ReadAllText(doc.FullPath).TrimEnd() + "\n\n" : "```jsonc\n{ \"layout\": 2 }\n```\n\n# Review findings\n\n";
            return existing + header + body;
        }

        /// <summary>Does the work a subagent brief asks for, in the repository the brief names.</summary>
        public void DoTask(string repoRoot, string task)
        {
            if (FixRequested && PathsEqual(repoRoot, Web))
            {
                TestGitFixture.Write(Path.Combine(Web, "test", "shout-trim.test.mjs"), """
                    import test from 'node:test';
                    import assert from 'node:assert/strict';
                    import { shout } from '../src/greet.mjs';
                    test('shout trims', () => { assert.equal(shout('  Ada '), 'Hello, Ada!'); });
                    """);
                TestGitFixture.Write(Path.Combine(Web, "src", "greet.mjs"), "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => `${greet(name.trim())}!`;\n");
                FixApplied = true;
                return;
            }

            switch (task)
            {
                case "T1":
                    TestGitFixture.Write(Path.Combine(Api, "test", "multiply.test.mjs"), """
                        import test from 'node:test';
                        import assert from 'node:assert/strict';
                        import { multiply } from '../dist/calc.mjs';
                        test('multiply', () => { assert.equal(multiply(3, 4), 12); });
                        """);
                    TestGitFixture.Write(Path.Combine(Api, "src", "calc.mjs"), "export const add = (a, b) => a + b;\nexport const multiply = (a, b) => a * b;\n");
                    return;
                case "T2":
                    TestGitFixture.Write(Path.Combine(Web, "test", "shout.test.mjs"), """
                        import test from 'node:test';
                        import assert from 'node:assert/strict';
                        import { shout } from '../src/greet.mjs';
                        test('shout', () => { assert.equal(shout('Ada'), 'Hello, Ada!'); });
                        """);
                    TestGitFixture.Write(Path.Combine(Web, "src", "greet.mjs"), "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => `${greet(name)}!`;\n");
                    return;
                default:
                    throw new InvalidOperationException($"the compliant model was asked to do an unknown task `{task}` in {repoRoot}");
            }
        }

        // ---------------------------------------------------------------- observable outcomes

        public void AssertCommitted(DocumentRef doc, string because)
        {
            Assert.True(Git(doc.Root, "status", "--porcelain", "--", doc.Relative).Length == 0, $"{because}: {doc.Relative} has uncommitted changes in {doc.Root}");
            Assert.True(Git(doc.Root, "log", "--format=%H", "-1", "--", doc.Relative).Length > 0, $"{because}: {doc.Relative} was never committed in {doc.Root}");
        }

        public void AssertHeadStatus(DocumentRef doc, string status, string because)
        {
            var head = Git(doc.Root, "show", $"HEAD:{doc.Relative}");
            Assert.True(Regex.IsMatch(head, $"\"status\"\\s*:\\s*\"{status}\""), $"{because}: {doc.Relative} at HEAD is not `{status}`:\n{head}");
        }

        public void AssertTaskCommitted(string repo, string task, Session session)
        {
            var log = Git(repo, "log", "--format=%s%n%b%n--", "main..HEAD");
            var subjects = Git(repo, "log", "--reverse", "--format=%s", "--grep", $"Plan-Task: {task}", "main..HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(subjects.Length >= 2, session.Transcript($"{task} must be committed in {Path.GetFileName(repo)} as a test commit then a feat commit; main..HEAD is:\n{log}"));
            Assert.StartsWith("test(", subjects[0], StringComparison.Ordinal);
            Assert.StartsWith("feat(", subjects[1], StringComparison.Ordinal);
            Assert.Equal($"change/{ChangeName(repo)}", Git(repo, "branch", "--show-current"));
        }

        public void AssertNotCommitted(string repo, string prefix, Session session)
        {
            var files = Git(repo, "ls-tree", "-r", "--name-only", "HEAD").Split('\n');
            Assert.False(files.Any(f => f.StartsWith(prefix, StringComparison.Ordinal)), session.Transcript($"{prefix} is gitignored and must never be committed in {Path.GetFileName(repo)}"));
        }

        public void AssertBuildLogHas(string changeFolder, string repo, string task, Session session)
        {
            var path = Path.Combine(changeFolder, "build", $"{repo}.md");
            Assert.True(File.Exists(path), session.Transcript($"the build log {path} must exist"));
            var root = Git(changeFolder, "rev-parse", "--show-toplevel");
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var committed = Git(root, "show", $"HEAD:{relative}");
            Assert.True(committed.Contains($"\"task\": \"{task}\"", StringComparison.Ordinal), session.Transcript($"the committed build log {relative} must have an entry for {task}:\n{committed}"));
        }

        public bool ReviewCommitted(string changeFolder, int number)
        {
            var root = Git(changeFolder, "rev-parse", "--show-toplevel");
            var relative = Path.GetRelativePath(root, Path.Combine(changeFolder, "build", "review.md")).Replace('\\', '/');
            var r = RunGit(root, "show", $"HEAD:{relative}");
            return r.ExitCode == 0 && r.Stdout.Contains($"## Review {number}", StringComparison.Ordinal);
        }

        public string ChangeName(string anyRepo)
        {
            var branches = Git(anyRepo, "for-each-ref", "--format=%(refname:short)", "refs/heads/change/");
            return branches.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(b => b["change/".Length..]).Single(b => b.EndsWith("-" + ChangeSlug, StringComparison.Ordinal));
        }

        public bool RemoteHasChangeBranch(string repo) =>
            Git(repo, "ls-remote", "--heads", "origin", $"refs/heads/change/*-{ChangeSlug}").Length > 0;

        public string? RemoteHead(string repo)
        {
            var line = Git(repo, "ls-remote", "--heads", "origin", $"refs/heads/change/*-{ChangeSlug}");
            return line.Length == 0 ? null : line.Split('\t')[0];
        }

        public string Head(string repo) => Git(repo, "rev-parse", "HEAD");

        public List<(string Repo, string Head, string Url)> PullRequests()
        {
            var state = Path.Combine(GhDir, "state.json");
            if (!File.Exists(state))
            {
                return [];
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(state));
            return doc.RootElement.GetProperty("prs").EnumerateArray()
                .Select(p => (p.GetProperty("repo").GetString()!, p.GetProperty("head").GetString()!, p.GetProperty("url").GetString()!))
                .ToList();
        }

        // ---------------------------------------------------------------- plumbing

        /// <summary>The last hook decisions each repository logged; for failure messages only, never for assertions.</summary>
        public string HookDecisions()
        {
            var lines = new List<string> { "--- hook decisions (.git/hoshin/hooks.jsonl, diagnostics only) ---" };
            foreach (var repo in new[] { Master, Api, Web })
            {
                var log = Path.Combine(repo, ".git", "hoshin", "hooks.jsonl");
                lines.Add($"[{Path.GetFileName(repo)}] " + (File.Exists(log) ? string.Join("\n  ", File.ReadAllLines(log).TakeLast(8)) : "(none)"));
            }

            return string.Join("\n", lines);
        }

        public string Git(string cwd, params string[] args)
        {
            var r = RunGit(cwd, args);
            Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)} in {cwd} failed: {r.Stderr}");
            return r.Stdout.Trim();
        }

        private static CommandOutcome RunGit(string cwd, params string[] args) =>
            TestServices.CommandRunner.Run(new CommandRequest(cwd, ["git", .. args], ExtraEnvironment: TestGitFixture.GitEnv));

        private void InitRepo(string path, Dictionary<string, string> files)
        {
            Directory.CreateDirectory(path);
            Git(path, "init", "-q", "-b", "main");
            Git(path, "config", "user.name", "Test User");
            Git(path, "config", "user.email", "test@example.com");
            foreach (var (name, text) in files)
            {
                TestGitFixture.Write(Path.Combine(path, name), text.Replace("\r\n", "\n", StringComparison.Ordinal));
            }

            Git(path, "add", ".");
            Git(path, "commit", "-q", "-m", "chore: initial");
            var remote = Path.Combine(Remotes, Path.GetFileName(path) + ".git");
            Git(Remotes, "init", "-q", "--bare", "-b", "main", remote);
            Git(path, "remote", "add", "origin", remote);
            Git(path, "push", "-q", "-u", "origin", "main");
            Git(path, "remote", "set-head", "origin", "main");
        }

        private void RunNode(string cwd, string script)
        {
            var r = TestServices.CommandRunner.Run(new CommandRequest(cwd, ["node", script]));
            Assert.True(r.ExitCode == 0, $"node {script} in {cwd} failed: {r.Stderr}");
        }

        public string RunHook(string cwd, string verb, string payload)
        {
            var args = new List<string> { HoshinDll };
            args.AddRange(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (verb.StartsWith("hook ", StringComparison.Ordinal))
            {
                args.Add("--roles");
                args.Add(RolesDir);
            }

            var env = new Dictionary<string, string>(TestGitFixture.GitEnv)
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["HOSHIN_GH"] = JsonSerializer.Serialize(new[] { "node", Path.Combine(GhDir, "gh.cjs") }),
                ["FAKE_GH_STATE"] = Path.Combine(GhDir, "state.json"),
                ["FAKE_GH_LOG"] = Path.Combine(GhDir, "log.jsonl"),
            };
            var r = TestServices.CommandRunner.Run(new CommandRequest(cwd, ["dotnet", .. args], StandardInput: payload, ExtraEnvironment: env));
            Assert.True(r.ExitCode == 0, $"`hoshin {verb}` must always exit 0 (a failing hook breaks the session); exit {r.ExitCode}:\n{r.Stderr}\n{r.Stdout}");

            // Exit 0 is not enough: the CLI swallows exceptions into one stderr line that neither the model nor the engineer sees.
            Assert.True(!r.Stderr.Contains("hoshin hook:", StringComparison.Ordinal), $"`hoshin {verb}` crashed and the crash was swallowed (the session sees nothing):\n{r.Stderr}");
            return r.Stdout.Trim();
        }

        public static string? PermissionDecision(string stdout)
        {
            if (stdout.Length == 0)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.TryGetProperty("hookSpecificOutput", out var h) && h.TryGetProperty("permissionDecision", out var d) ? d.GetString() : null;
        }

        public static bool PathsEqual(string a, string b) =>
            string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        public void Dispose() => _temp.Dispose();

        private const string FakeGh = """
            const fs = require('fs'), path = require('path');
            const args = process.argv.slice(2), repo = path.basename(process.cwd());
            const stateFile = process.env.FAKE_GH_STATE;
            const state = fs.existsSync(stateFile) ? JSON.parse(fs.readFileSync(stateFile, 'utf8')) : { prs: [] };
            fs.appendFileSync(process.env.FAKE_GH_LOG, JSON.stringify({ repo, args }) + '\n');
            const opt = (name) => args[args.indexOf(name) + 1];
            if (args[0] === 'auth') process.exit(0);
            if (args[0] === 'pr' && args[1] === 'list') {
              const found = state.prs.filter((p) => p.repo === repo && p.head === opt('--head')).map(({ number, url }) => ({ number, url }));
              console.log(JSON.stringify(found.slice(0, 1)));
            } else if (args[0] === 'pr' && args[1] === 'create') {
              const number = state.prs.length + 1, url = 'https://github.com/example/' + repo + '/pull/' + number;
              state.prs.push({ repo, number, url, head: opt('--head'), base: opt('--base'), title: opt('--title') });
              fs.writeFileSync(stateFile, JSON.stringify(state));
              console.log(url);
            } else if (args[0] === 'pr' && args[1] === 'view') {
              console.log(JSON.stringify({ state: 'OPEN' }));
            } else process.exit(3);
            """;
    }

    /// <summary>The path and folder of a document a brief asked for.</summary>
    private sealed record DocumentRef(string Root, string Relative)
    {
        public string FullPath => Path.GetFullPath(Path.Combine(Root, Relative));
        public string FileName => Path.GetFileName(Relative);
        public string Folder => Path.GetDirectoryName(FullPath)!;
    }

    /// <summary>One Claude Code session: a compliant model that does exactly what the hooks inject, and nothing else.</summary>
    private sealed class Session(ShopWorkspace ws, string id, string cwd)
    {
        private static readonly Regex WriteRequest = new(@"Write (?:it|the review section) to `(?<rel>[^`]+)` in (?<root>.+?)(?: \(|, from|\.\s|\.$)", RegexOptions.Multiline);
        private static readonly Regex TaskHeading = new(@"^# Task (?<id>\S+?):", RegexOptions.Multiline);
        private static readonly Regex RepositoryLine = new(@"^Repository: (?<path>.+?)\s*$", RegexOptions.Multiline);
        private readonly List<string> _log = [];

        public DocumentRef? LastDocument { get; private set; }

        /// <summary>The engineer types a slash command.</summary>
        public string? Prompt(string skill, string args = "") => Context(Hook("hook start", PromptPayload(skill, args)));

        /// <summary>The model invokes a skill on its own (not typed by the engineer).</summary>
        public string? ModelInvokedSkill(string skill) => Context(Hook("hook start", JsonSerializer.Serialize(new
        {
            session_id = id,
            transcript_path = $"/tmp/{id}.jsonl",
            cwd,
            permission_mode = "default",
            hook_event_name = "PostToolUse",
            tool_name = "Skill",
            tool_use_id = "toolu_skill",
            tool_input = new { skill = $"hoshin:{skill}" },
            tool_response = new { success = true, commandName = $"hoshin:{skill}" },
        })));

        /// <summary>Plays the rest of the turn: dispatch what is asked, write what is asked, then try to end the turn.</summary>
        public void Turn(string? context)
        {
            var pending = context;
            for (var round = 0; round < MaxHookRoundsPerTurn; round++)
            {
                while (pending is not null && pending.Contains(ShopWorkspace.BeginMarker, StringComparison.Ordinal))
                {
                    pending = Dispatch(pending);
                }

                if (pending is not null && WriteRequest.Match(pending) is { Success: true } write)
                {
                    var doc = new DocumentRef(write.Groups["root"].Value.Trim(), write.Groups["rel"].Value.Trim());
                    ws.WriteDocument(doc, pending);
                    LastDocument = doc;
                }

                var stop = Hook("hook stop", StopPayload(stopHookActive: round > 0));
                pending = BlockReason(stop);
                if (pending is null)
                {
                    return;
                }
            }

            throw Fail($"the session was never allowed to end its turn after {MaxHookRoundsPerTurn} Stop hooks");
        }

        public void AssertTurnEndsQuietly(string because)
        {
            var stop = Hook("hook stop", StopPayload(stopHookActive: false));
            Assert.True(BlockReason(stop) is null, Transcript($"{because}; Stop was blocked"));
        }

        private string? Dispatch(string context)
        {
            Assert.DoesNotContain("is not accepted", context, StringComparison.Ordinal);
            var start = context.IndexOf(ShopWorkspace.BeginMarker, StringComparison.Ordinal);
            var end = context.IndexOf(ShopWorkspace.EndMarker, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, Transcript("a dispatch must carry the subagent prompt between both markers"));
            var prompt = context[(start + ShopWorkspace.BeginMarker.Length)..end].Trim('\r', '\n');
            var task = TaskHeading.Match(prompt) is { Success: true } t ? t.Groups["id"].Value : "?";
            var repo = RepositoryLine.Match(prompt) is { Success: true } r ? r.Groups["path"].Value : "";
            Assert.True(
                ShopWorkspace.PathsEqual(repo, cwd),
                Transcript($"a session started in {Path.GetFileName(cwd)} must only build {Path.GetFileName(cwd)}'s tasks, but it dispatched {task} for `{repo}` (its CLAUDE.md, skills and hooks are not loaded here)"));

            ws.DoTask(cwd, task);
            var result = Context(Hook("hook result", JsonSerializer.Serialize(new
            {
                session_id = id,
                transcript_path = $"/tmp/{id}.jsonl",
                cwd,
                permission_mode = "default",
                hook_event_name = "PostToolUse",
                tool_name = "Agent",
                tool_use_id = $"toolu_agent_{task}",
                tool_input = new { subagent_type = "general-purpose", description = $"Build {task}", prompt },
                tool_response = new { status = "completed", content = new[] { new { type = "text", text = $"Wrote the test first, then the change for {task}." } } },
            })));
            if (result is not null)
            {
                Assert.True(!result.Contains("is not accepted", StringComparison.Ordinal), Transcript($"{task} done correctly by the subagent must be accepted"));
            }

            return result;
        }

        public string PromptPayload(string skill, string args) => JsonSerializer.Serialize(new
        {
            session_id = id,
            transcript_path = $"/tmp/{id}.jsonl",
            cwd,
            permission_mode = "default",
            hook_event_name = "UserPromptExpansion",
            command_name = $"hoshin:{skill}",
            command_args = args,
            prompt = args.Length == 0 ? $"/hoshin:{skill}" : $"/hoshin:{skill} {args}",
        });

        public string StopPayload(bool stopHookActive) => JsonSerializer.Serialize(new
        {
            session_id = id,
            transcript_path = $"/tmp/{id}.jsonl",
            cwd,
            permission_mode = "default",
            hook_event_name = "Stop",
            stop_hook_active = stopHookActive,
            background_tasks = Array.Empty<object>(),
            last_assistant_message = "Done.",
        });

        public string ToolPayload(string tool, object input) => JsonSerializer.Serialize(new
        {
            session_id = id,
            transcript_path = $"/tmp/{id}.jsonl",
            cwd,
            permission_mode = "default",
            hook_event_name = "PreToolUse",
            tool_name = tool,
            tool_use_id = $"toolu_{tool}",
            tool_input = input,
        });

        public string Hook(string verb, string payload)
        {
            var stdout = ws.RunHook(cwd, verb, payload);
            _log.Add($"[{id} @ {Path.GetFileName(cwd)}] {verb} {Summarize(payload)}\n  -> {(stdout.Length == 0 ? "(silent)" : stdout)}");
            return stdout;
        }

        public string? Context(string stdout)
        {
            if (stdout.Length == 0 || stdout == "{}")
            {
                return null;
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("hookSpecificOutput", out var h) && h.TryGetProperty("additionalContext", out var c))
            {
                return c.GetString();
            }

            return root.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }

        private static string? BlockReason(string stdout)
        {
            if (stdout.Length == 0 || stdout == "{}")
            {
                return null;
            }

            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.TryGetProperty("decision", out var d) && d.GetString() == "block"
                ? doc.RootElement.GetProperty("reason").GetString()
                : null;
        }

        private static string Summarize(string payload)
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            return r.GetProperty("hook_event_name").GetString() switch
            {
                "UserPromptExpansion" => r.GetProperty("prompt").GetString()!,
                "PostToolUse" => $"{r.GetProperty("tool_name").GetString()} result",
                "PreToolUse" => $"{r.GetProperty("tool_name").GetString()} {r.GetProperty("tool_input")}",
                "Stop" => $"Stop (stop_hook_active={r.GetProperty("stop_hook_active").GetBoolean()})",
                var other => other ?? "?",
            };
        }

        public string Transcript(string message) =>
            $"{message}\n\n--- hook transcript of session {id} ---\n{string.Join("\n", _log.TakeLast(16))}\n\n{ws.HookDecisions()}";

        public Exception Fail(string message) => new Xunit.Sdk.XunitException(Transcript(message));
    }
}
