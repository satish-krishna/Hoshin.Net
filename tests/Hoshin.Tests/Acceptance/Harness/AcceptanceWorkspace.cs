using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Tests.Unit;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Acceptance.Harness;

/// <summary>A product repository of the workspace: its committed files, and what the engineer has done in it locally.</summary>
/// <param name="Prepare">Runs once after the clone, as an engineer who works in the repository has (a build, an install).</param>
internal sealed record RepoSpec(string Name, IReadOnlyDictionary<string, string> Files, Action<AcceptanceWorkspace, string>? Prepare = null);

/// <summary>The shape of a workspace: its master, its product repositories, the checks every build finishes with, and the change.</summary>
internal sealed record WorkspaceSpec(ChangeScript Script, IReadOnlyList<RepoSpec> Repos)
{
    public string MasterName { get; init; } = "ai-sdlc";

    public string SystemName { get; init; } = "shop";

    /// <summary>The <c>build.finish</c> check every repository's build ends with.</summary>
    public string[] Check { get; init; } = ["node", "--test"];

    /// <summary>One repository is its own master, with one app at its root (<c>layout: single-repo</c>).</summary>
    public bool SingleRepo { get; init; }

    /// <summary>The R0 workspace: <c>ai-sdlc</c> (master), <c>api</c> and <c>web</c>.</summary>
    public static WorkspaceSpec Shop(ChangeScript script) => new(script, [ShopRepos.Api, ShopRepos.Web]);

    /// <summary>A single-repository workspace: <c>shop</c> is the master and its one app, <c>app</c>, is the repository root.</summary>
    public static WorkspaceSpec SingleRepoShop(ChangeScript script) => new(script, [ShopRepos.SingleApp]) { MasterName = "shop", SingleRepo = true };
}

/// <summary>The product repositories of the shop workspace, as an engineer has them checked out.</summary>
internal static class ShopRepos
{
    public static readonly RepoSpec Api = new(
        "api",
        new Dictionary<string, string>
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
        },
        (ws, path) => ws.RunNode(path, "build.mjs")); // The engineer has built api once, as anyone working in it has.

    public static readonly RepoSpec Web = new(
        "web",
        new Dictionary<string, string>
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
        },
        (_, path) => InstallTestkit(path));

    /// <summary>The single-repository shop: the greeting app at the repository root.</summary>
    public static readonly RepoSpec SingleApp = new(
        "app",
        new Dictionary<string, string>
        {
            ["package.json"] = "{ \"name\": \"shop\", \"type\": \"module\", \"private\": true }\n",
            ["src/greet.mjs"] = "export const greet = (name) => `Hello, ${name}`;\n",
            ["test/greet.test.mjs"] = """
                import test from 'node:test';
                import assert from 'node:assert/strict';
                import { greet } from '../src/greet.mjs';
                test('greet', () => { assert.equal(greet('Ada'), 'Hello, Ada'); });
                """,
        });

    /// <summary>An installed dev tool, gitignored, as <c>npm install</c> leaves it.</summary>
    public static void InstallTestkit(string repo) =>
        TestGitFixture.Write(Path.Combine(repo, "node_modules", "testkit", "run.mjs"), """
            import { spawnSync } from 'node:child_process';
            const r = spawnSync(process.execPath, ['--test', ...process.argv.slice(2)], { stdio: 'inherit' });
            process.exit(r.status ?? 1);
            """);
}

/// <summary>
/// A real workspace for the acceptance gate: a master repository not named <c>master</c> and product repositories,
/// each with a bare remote outside the workspace, plus a fake <c>gh</c> that records pull requests. Every hook runs the
/// shipped <c>hoshin.dll</c> exactly as the harness does. Assertions read only what an engineer can see: commits,
/// branches, remote refs, committed documents, pull requests, folders, and what the session was told.
/// </summary>
internal sealed class AcceptanceWorkspace : IDisposable
{
    public const string BeginMarker = "<<<BEGIN SUBAGENT PROMPT";
    public const string EndMarker = "END SUBAGENT PROMPT>>>";

    public static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string HoshinDll = Path.Combine(AppContext.BaseDirectory, "hoshin.dll");
    private static readonly string RolesDir = Path.Combine(RepoRoot, "skills");
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly TempDir? _temp;
    private readonly Dictionary<string, string> _repos = new(StringComparer.Ordinal);

    private AcceptanceWorkspace(WorkspaceSpec spec, string root, string remotes, string ghDir, TempDir? temp)
    {
        Spec = spec;
        _temp = temp;
        Root = root;
        Remotes = remotes;
        GhDir = ghDir;
        Master = Path.Combine(Root, spec.MasterName);
        foreach (var repo in spec.Repos)
        {
            _repos[repo.Name] = spec.SingleRepo ? Master : Path.Combine(Root, repo.Name);
        }
    }

    public WorkspaceSpec Spec { get; }

    public ChangeScript Script => Spec.Script;

    public string Root { get; }

    public string Remotes { get; }

    public string Master { get; }

    public string GhDir { get; }

    public string Api => Repo("api");

    public string Web => Repo("web");

    /// <summary>Set by a test before the engineer asks for a fix; the compliant model then applies <see cref="ChangeScript.Fix"/>.</summary>
    public bool FixRequested { get; set; }

    public bool FixApplied { get; private set; }

    /// <summary>Set by a test whose path makes an attempt fail on purpose; otherwise every dispatch must be accepted.</summary>
    public bool RejectionsExpected { get; set; }

    /// <summary>Runs after the compliant subagent has done a task and before the harness reports its result: what the engineer does meanwhile.</summary>
    public Action<string, string, int>? AfterTask { get; set; }

    /// <summary>Every dispatch any session made, in order: the session, the repository folder, the task and the attempt.</summary>
    public List<(string Session, string Repository, string Task, int Attempt, string Prompt)> Dispatches { get; } = [];

    public IReadOnlyCollection<string> ProductRepoNames => _repos.Keys;

    public string Repo(string name) => _repos.TryGetValue(name, out var path) ? path : throw new InvalidOperationException($"the workspace has no repository `{name}`");

    public static AcceptanceWorkspace Create(WorkspaceSpec spec, IReadOnlyCollection<string>? notCheckedOut = null)
    {
        var temp = new TempDir("hoshin-acc-");
        var ws = new AcceptanceWorkspace(spec, Path.Combine(temp.Path, "ws"), Path.Combine(temp.Path, "remotes"), Path.Combine(temp.Path, "gh"), temp);
        Directory.CreateDirectory(ws.Root);
        Directory.CreateDirectory(ws.Remotes);
        Directory.CreateDirectory(ws.GhDir);
        File.WriteAllText(Path.Combine(ws.GhDir, "gh.cjs"), FakeGh);

        var marker = new Dictionary<string, object> { ["version"] = 1 };
        if (spec.SingleRepo)
        {
            marker["layout"] = "single-repo";
            marker["systems"] = new Dictionary<string, object> { [spec.SystemName] = new { apps = spec.Repos.ToDictionary(r => r.Name, _ => ".") } };
        }
        else
        {
            marker["systems"] = new Dictionary<string, object> { [spec.SystemName] = new { repos = spec.Repos.Select(r => r.Name).ToArray() } };
        }

        marker["hooks"] = new Dictionary<string, object> { ["build.finish"] = new[] { new { id = "unit", run = spec.Check } } };
        var masterFiles = new Dictionary<string, string>
        {
            [".hoshin.json"] = JsonSerializer.Serialize(marker, IndentedJson) + "\n",
            [$"intent/{spec.SystemName}/policies.md"] = "# Policies\n\nNone yet.\n",
        };

        if (spec.SingleRepo)
        {
            foreach (var (name, text) in spec.Repos.Single().Files)
            {
                masterFiles[name] = text;
            }

            ws.InitRepo(ws.Master, masterFiles);
            spec.Repos.Single().Prepare?.Invoke(ws, ws.Master);
            return ws;
        }

        ws.InitRepo(ws.Master, masterFiles);
        foreach (var repo in spec.Repos)
        {
            ws.InitRepo(ws.Repo(repo.Name), repo.Files);
            repo.Prepare?.Invoke(ws, ws.Repo(repo.Name));
        }

        foreach (var name in notCheckedOut ?? [])
        {
            ws.RemoveCheckout(name);
        }

        return ws;
    }

    /// <summary>
    /// The same workspace on another machine: every repository cloned from its remote into a new folder, on the change
    /// branch when the remote has one, prepared as the engineer would, with no local hoshin state. It shares the
    /// remotes and the pull requests with this workspace.
    /// </summary>
    public AcceptanceWorkspace CloneOnAnotherMachine(string folder)
    {
        var clone = new AcceptanceWorkspace(Spec, Path.Combine(Path.GetDirectoryName(Root)!, folder), Remotes, GhDir, temp: null);
        Directory.CreateDirectory(clone.Root);
        foreach (var path in new[] { clone.Master }.Concat(Spec.Repos.Select(r => clone.Repo(r.Name))).Distinct(StringComparer.Ordinal))
        {
            var remote = Path.Combine(Remotes, Path.GetFileName(path) + ".git");
            Git(clone.Root, "clone", "-q", remote, path);
            Git(path, "config", "user.name", "Test User");
            Git(path, "config", "user.email", "test@example.com");
            var branch = Git(path, "ls-remote", "--heads", "origin", $"refs/heads/change/*-{Script.Slug}");
            if (branch.Length > 0)
            {
                var name = branch.Split('\t')[1].Trim()["refs/heads/".Length..];
                Git(path, "switch", "-q", name);
            }
        }

        foreach (var repo in Spec.Repos)
        {
            repo.Prepare?.Invoke(clone, clone.Repo(repo.Name));
        }

        return clone;
    }

    public void RemoveCheckout(string name)
    {
        var path = Repo(name);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    public Session Session(string id, string cwd) => new(this, id, cwd);

    /// <summary>Intent, spec and plan written and accepted from the master repository; returns the plan.</summary>
    public DocumentRef AuthorAndAcceptAll(Session po)
    {
        DocumentRef? doc = null;
        foreach (var skill in new[] { "intent", "spec", "plan" })
        {
            doc = AuthorAndAccept(po, skill);
        }

        return doc!;
    }

    /// <summary>One document written and accepted from the master repository.</summary>
    public DocumentRef AuthorAndAccept(Session po, string skill)
    {
        po.Turn(skill == "intent" ? po.Prompt("intent", Script.Slug) : po.Prompt(skill));
        var doc = po.LastDocument;
        Assert.True(doc?.FileName == $"{skill}.md", po.Transcript($"/hoshin:{skill} must ask for {skill}.md to be written"));
        AssertCommitted(doc!, po.Transcript($"the {skill} draft is committed when the turn ends"));
        po.Turn(po.Prompt("accept"));
        AssertHeadStatus(doc!, "accepted", po.Transcript($"the engineer's /hoshin:accept accepts the {skill}"));
        return doc!;
    }

    // ---------------------------------------------------------------- the compliant model

    /// <summary>Writes the document a brief asks for, as a compliant model would.</summary>
    public void WriteDocument(DocumentRef doc, string brief)
    {
        string FrontMatter(string startsWith) =>
            (brief.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(startsWith, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"the brief gives no front matter starting with {startsWith}:\n{brief}"))
            .Replace("<short title>", Script.Title, StringComparison.Ordinal)
            .Replace("<intent title>", Script.Title, StringComparison.Ordinal)
            .Replace("<name and role>", "Pat, product owner", StringComparison.Ordinal)
            .Replace("<YYYY-MM-DD>", Today, StringComparison.Ordinal);

        var text = doc.FileName switch
        {
            "intent.md" => IntentText(FrontMatter("{ \"title\"")),
            "spec.md" => SpecText(FrontMatter("{ \"layout\": 2, \"title\"")),
            "plan.md" => PlanText(FrontMatter("{ \"layout\": 2, \"system\"")),
            "review.md" => ReviewText(doc, brief),
            _ => throw new InvalidOperationException($"the compliant model does not know how to write {doc.FileName}"),
        };
        TestGitFixture.Write(doc.FullPath, text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    /// <summary>
    /// Writes the light-track documents a fix brief asks for. The brief names the three files but gives no front matter,
    /// so the model writes what the light track needs: <c>track: fix</c>, the scope the brief names, and drafts.
    /// </summary>
    public void WriteFixDocuments(IReadOnlyList<DocumentRef> docs, string brief)
    {
        var scope = Regex.Match(brief, "scope: \"(?<s>[^\"]+)\"") is { Success: true } m ? m.Groups["s"].Value : Spec.SystemName;
        foreach (var doc in docs)
        {
            var system = SystemOf(doc);
            var text = doc.FileName switch
            {
                "intent.md" => IntentText($"{{ \"title\": \"{Script.Title}\", \"system\": \"{system}\", \"author\": \"Eve, engineer\", \"date\": \"{Today}\", \"status\": \"draft\", \"scope\": \"{scope}\", \"track\": \"fix\" }}"),
                "spec.md" => SpecText($"{{ \"layout\": 2, \"title\": \"{Script.Title}\", \"system\": \"{system}\", \"date\": \"{Today}\", \"status\": \"draft\" }}"),
                "plan.md" => PlanText($"{{ \"layout\": 2, \"system\": \"{system}\", \"date\": \"{Today}\", \"status\": \"draft\" }}"),
                _ => throw new InvalidOperationException($"a fix brief asked for {doc.FileName}"),
            };
            TestGitFixture.Write(doc.FullPath, text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        }
    }

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private string SystemOf(DocumentRef doc)
    {
        var parts = doc.Relative.Replace('\\', '/').Split('/');
        return parts.Length >= 3 && parts[0] == "intent" ? parts[1] : Spec.SystemName;
    }

    public string IntentText(string frontMatter) => $$"""
        ```jsonc
        {{frontMatter}}
        ```

        # Intent: {{Script.Title}}

        ## Problem

        {{Script.Problem}}

        ## Proposed outcome

        {{Script.Outcome}}

        ## Affected users and systems

        {{Script.Users}}

        ## Constraints

        {{Script.Constraints}}

        ## Decisions

        {{Script.Decisions}}

        ## Open questions

        None.
        """;

    public string SpecText(string frontMatter) => $$"""
        ```jsonc
        {{frontMatter}}
        ```

        # Spec: {{Script.Title}}

        ## Requirements

        ```gherkin
        {{Script.Requirements}}
        ```
        """;

    public string PlanText(string frontMatter)
    {
        static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => JsonSerializer.Serialize(i))) + "]";
        var tasks = string.Join("\n\n", Script.Tasks.Select(t =>
        {
            var compile = t.Compile is null ? "" : $"\n  \"compile\": {List(t.Compile)},";
            return $$"""
                ### {{t.Id}}: {{t.Title}}

                ```jsonc
                {
                  "id": "{{t.Id}}",
                  "repo": "{{t.Repo}}",
                  "depends_on": {{List(t.DependsOn)}},
                  "covers": {{List(t.Covers)}},{{compile}}
                  "test": {{List(t.Test)}}
                }
                ```

                #### Change

                {{t.Change}}

                #### Tests

                {{t.Tests}}
                """;
        }));
        return $$"""
            ```jsonc
            {{frontMatter}}
            ```

            # Plan: {{Script.Title}}

            ## Tasks

            {{tasks}}
            """;
    }

    private string ReviewText(DocumentRef doc, string brief)
    {
        var number = int.Parse(Regex.Match(brief, @"\(Review (\d+)\)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var commits = string.Join(", ", ProductRepoNames.Order(StringComparer.Ordinal).Select(r => $"\"{r}\": \"{Head(BuildFolder(r))[..7]}\""));
        var header = $$"""
            ## Review {{number}}

            ```jsonc
            { "review": {{number}}, "reviewed_at": "{{DateTime.UtcNow:yyyy-MM-dd}}", "commits": { {{commits}} } }
            ```
            """;
        var finding = Script.FirstReviewFinding;
        var body = number == 1 && finding is not null
            ? $$"""

                ### F1

                ```jsonc
                { "id": "F1", "severity": "high", "repo": "{{finding.Repo}}", "concern": "bug", "file": "{{finding.File}}", "line": {{finding.Line}} }
                ```

                {{finding.Text}}
                """
            : "\nNo findings.\n";
        var existing = File.Exists(doc.FullPath) ? File.ReadAllText(doc.FullPath).TrimEnd() + "\n\n" : "```jsonc\n{ \"layout\": 2 }\n```\n\n# Review findings\n\n";
        return existing + header + body;
    }

    /// <summary>Does the work a subagent brief asks for, in the repository folder the brief names.</summary>
    public void DoTask(string repository, string task)
    {
        if (FixRequested && Script.Fix is not null && (Spec.SingleRepo || PathsEqual(repository, Repo(Script.FixRepo!))))
        {
            Script.Fix(repository);
            FixApplied = true;
            return;
        }

        Script.Task(task).Do(repository);
    }

    // ---------------------------------------------------------------- observable outcomes

    /// <summary>Where a product repository's change is built: the repository itself, or, in a single-repository workspace, the change's copy of it.</summary>
    public string BuildFolder(string repoName) =>
        Spec.SingleRepo && ChangeCopy() is { } copy ? copy : Repo(repoName);

    /// <summary>The change's own copy of the master repository, when it has one (<c>&lt;workspace&gt;/.hoshin/copies/&lt;change&gt;</c>).</summary>
    public string? ChangeCopy()
    {
        var copies = Path.Combine(Root, ".hoshin", "copies");
        if (!Directory.Exists(copies))
        {
            return null;
        }

        var all = Directory.EnumerateDirectories(copies).ToList();
        return all.Count == 1 ? all[0] : all.SingleOrDefault(d => IsThisChange(Path.GetFileName(d)));
    }

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

    public bool HeadStatusIs(DocumentRef doc, string status)
    {
        var r = RunGit(doc.Root, "show", $"HEAD:{doc.Relative}");
        return r.ExitCode == 0 && Regex.IsMatch(r.Stdout, $"\"status\"\\s*:\\s*\"{status}\"");
    }

    public void AssertTaskCommitted(string repo, string task, Session session)
    {
        var log = Git(repo, "log", "--format=%s%n%b%n--", "main..HEAD");
        var subjects = Git(repo, "log", "--reverse", "--format=%s", "--grep", $"Plan-Task: {task}", "main..HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(subjects.Length >= 2, session.Transcript($"{task} must be committed in {Path.GetFileName(repo)} as a test commit then a feat commit; main..HEAD is:\n{log}"));
        Assert.StartsWith("test(", subjects[0], StringComparison.Ordinal);
        Assert.Matches(@"^(?:feat|fix)\(", subjects[1]); // the production commit takes the change's type: feat, or fix on the light track
        Assert.Equal($"change/{ChangeName(repo)}", Git(repo, "branch", "--show-current"));
    }

    /// <summary>The subjects of commits on the change branch that carry a <c>Plan-Task</c> trailer, oldest first.</summary>
    public string[] TaskCommits(string repo) =>
        RunGit(repo, "log", "--reverse", "--format=%s", "--grep", "Plan-Task: ", "main..HEAD") is { ExitCode: 0 } r
            ? r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

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
        var names = ChangeBranches(anyRepo).Select(b => b["change/".Length..]).ToList();
        return names.Count == 1 ? names[0] : names.Single(IsThisChange);
    }

    /// <summary>
    /// Whether a change name is this script's change. The intent track names a change after the slug the engineer
    /// typed; the light track names it after the request's words, so a workspace with one change needs no match.
    /// </summary>
    private bool IsThisChange(string changeName) => changeName.EndsWith("-" + Script.Slug, StringComparison.Ordinal);

    /// <summary>Every local change branch of a repository, whatever change it belongs to.</summary>
    public string[] ChangeBranches(string repo) =>
        Git(repo, "for-each-ref", "--format=%(refname:short)", "refs/heads/change/").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public bool RemoteHasChangeBranch(string repo) => RemoteHead(repo) is not null;

    public string? RemoteHead(string repo)
    {
        var lines = Git(repo, "ls-remote", "--heads", "origin", "refs/heads/change/*").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = lines.Length == 1 ? lines[0] : lines.SingleOrDefault(l => IsThisChange(l.Split('\t')[1].Trim()["refs/heads/change/".Length..]));
        return line?.Split('\t')[0];
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
        foreach (var repo in new[] { Master }.Concat(_repos.Values).Distinct(StringComparer.Ordinal))
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

    public static CommandOutcome RunGit(string cwd, params string[] args) =>
        TestServices.CommandRunner.Run(new CommandRequest(cwd, ["git", .. args], ExtraEnvironment: TestGitFixture.GitEnv));

    private void InitRepo(string path, IReadOnlyDictionary<string, string> files)
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

    public void RunNode(string cwd, string script)
    {
        var r = TestServices.CommandRunner.Run(new CommandRequest(cwd, ["node", script]));
        Assert.True(r.ExitCode == 0, $"node {script} in {cwd} failed: {r.Stderr}");
    }

    /// <summary>Runs the shipped CLI as the harness runs a hook: payload on stdin, and it must always exit 0 without a swallowed crash.</summary>
    public string RunHook(string cwd, string verb, string payload)
    {
        var args = new List<string> { HoshinDll };
        args.AddRange(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (verb.StartsWith("hook ", StringComparison.Ordinal))
        {
            args.Add("--roles");
            args.Add(RolesDir);
        }

        var r = TestServices.CommandRunner.Run(new CommandRequest(cwd, ["dotnet", .. args], StandardInput: payload, ExtraEnvironment: Environment()));
        Assert.True(r.ExitCode == 0, $"`hoshin {verb}` must always exit 0 (a failing hook breaks the session); exit {r.ExitCode}:\n{r.Stderr}\n{r.Stdout}");

        // Exit 0 is not enough: the CLI swallows exceptions into one stderr line that neither the model nor the engineer sees.
        Assert.True(!r.Stderr.Contains("hoshin hook:", StringComparison.Ordinal), $"`hoshin {verb}` crashed and the crash was swallowed (the session sees nothing):\n{r.Stderr}");
        return r.Stdout.Trim();
    }

    /// <summary>Runs a CLI command the engineer types (not a hook), and returns its exit code and output.</summary>
    public CommandOutcome RunCli(string cwd, params string[] args) =>
        TestServices.CommandRunner.Run(new CommandRequest(cwd, ["dotnet", HoshinDll, .. args], ExtraEnvironment: Environment()));

    private Dictionary<string, string> Environment() => new(TestGitFixture.GitEnv)
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["HOSHIN_GH"] = JsonSerializer.Serialize(new[] { "node", Path.Combine(GhDir, "gh.cjs") }),
        ["FAKE_GH_STATE"] = Path.Combine(GhDir, "state.json"),
        ["FAKE_GH_LOG"] = Path.Combine(GhDir, "log.jsonl"),
    };

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

    public static bool IsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parent = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public void Dispose() => _temp?.Dispose();

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
internal sealed record DocumentRef(string Root, string Relative)
{
    public string FullPath => Path.GetFullPath(Path.Combine(Root, Relative));

    public string FileName => Path.GetFileName(Relative);

    public string Folder => Path.GetDirectoryName(FullPath)!;
}
