using System.Text.RegularExpressions;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Events.Steps;

/// <summary><c>change.done</c>: verifies full readiness, runs stale <c>build.finish</c>, pushes branches, opens PRs, and records them in <c>change.jsonc</c>.</summary>
public sealed class ChangeDoneStep(
    IGitClient? git = null,
    IPullRequestClient? prClient = null,
    ProgressService? progress = null,
    ReviewReader? reviewReader = null,
    AppService? apps = null,
    Func<EventContext, EventRunOptions?, Task<EventOutcome>>? runEventCallback = null) : IEventStep
{
    private static readonly HashSet<string> BlockingSeverities = ["critical", "high"];
    private static readonly Regex NothingToCommit = new(@"nothing to commit|no changes added", RegexOptions.Compiled);

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;
    private readonly IPullRequestClient _pr = prClient ?? GhPullRequestClient.Default;
    private readonly ProgressService _progress = progress ?? ProgressService.Default;
    private readonly ReviewReader _review = reviewReader ?? new ReviewReader();
    private readonly AppService _apps = apps ?? new AppService(git);
    private Func<EventContext, EventRunOptions?, Task<EventOutcome>>? _runEvent = runEventCallback;

    public string EventName => "change.done";
    public bool ReusePassed => false;

    internal void BindRunner(Func<EventContext, EventRunOptions?, Task<EventOutcome>> runEvent) => _runEvent = runEvent;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public async Task<List<string>> CheckAsync(EventContext ctx)
    {
        if (_apps.IsSingleRepo(ctx.Master))
        {
            return await CheckSingleRepoAsync(ctx).ConfigureAwait(false);
        }

        var record = RecordOf(ctx);
        var branch = record.Branch;
        var repos = _progress.PlanRepos(ctx.Folder);
        if (repos.Count == 0)
        {
            return [$"{ctx.Change} has no plan that reads cleanly, so there is nothing to declare done"];
        }

        var problems = new List<string>();
        var masterBranch = _git.CurrentBranch(ctx.Master);
        if (masterBranch != branch)
        {
            problems.Add($"the master copy {ctx.Master} is on {masterBranch ?? "a detached HEAD"}, not on {branch}");
        }

        var masterDirty = _git.DirtyPaths(ctx.Master);
        if (masterDirty.Count > 0)
        {
            problems.Add($"the master copy has uncommitted files: {string.Join(", ", masterDirty)}");
        }

        foreach (var repo in repos.Where(r => r != "master"))
        {
            var path = Path.Combine(ctx.Workspace, repo);
            var missing = _progress.UnloggedTasks(ctx.Folder, repo) ?? [];
            if (missing.Count > 0)
            {
                problems.Add($"{repo} still has tasks without a build log entry: {string.Join(", ", missing)}");
            }

            if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git")))
            {
                problems.Add($"{repo} is not checked out in {ctx.Workspace}");
            }
            else
            {
                if (!_git.HasLocalBranch(path, branch))
                {
                    problems.Add($"{repo} has no branch {branch}");
                }

                var dirty = _git.DirtyPaths(path);
                if (dirty.Count > 0)
                {
                    problems.Add($"{repo} has uncommitted files: {string.Join(", ", dirty)}");
                }
            }
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        var repoList = Touched(ctx, branch);
        var scope = ScopeRules.ChangeScope(ctx.Master, ctx.System, ctx.Folder);
        foreach (var r in repoList)
        {
            var broken = ScopeRules.CommitRuleProblems(_git.CommitsBeyondBase(r.Path, $"refs/heads/{branch}", _git.BaseBranch(r.Path)!), scope);
            if (broken.Count > 0)
            {
                problems.Add($"commits on {r.Repo}'s {branch} break the commit rules: {string.Join("; ", broken)}");
            }
        }

        var latest = LatestReview(ctx.Folder);
        if (latest is null)
        {
            problems.Add($"{ctx.Change} has no review yet: run the hoshin review skill first");
        }
        else
        {
            var blocking = BlockingFindings(latest);
            if (blocking.Count > 0)
            {
                problems.Add($"the latest review (review {latest.Number}) has unresolved high findings: {string.Join("; ", blocking)}. Fix them and review again; a later review that no longer finds them clears them");
            }

            foreach (var r in repoList)
            {
                latest.Commits.TryGetValue(r.Repo, out var at);
                if (string.IsNullOrEmpty(at) || !r.Head.StartsWith(at, StringComparison.Ordinal))
                {
                    problems.Add($"the latest review covered {r.Repo} at {at ?? "no commit"}, but its branch is now at {r.Head[..Math.Min(7, r.Head.Length)]}: run the review again");
                }
            }
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        foreach (var r in repoList)
        {
            if (_git.CurrentBranch(r.Path) != branch)
            {
                problems.Add($"{r.Repo} is not on {branch}, so its checks cannot run on the work being declared done");
                continue;
            }

            var subCtx = ctx.CloneFor("build.finish", r.Repo, r.Path, r.Path, branch, _git.BaseBranch(r.Path), r.Head);
            var checks = await _runEvent!(subCtx, null).ConfigureAwait(false);
            if (checks.Outcome != "passed")
            {
                var detail = string.Join('\n', checks.Problems.Append(checks.Failed?.Output ?? "").Where(s => s.Length > 0));
                problems.Add($"{r.Repo}'s checks (build.finish) did not pass at {r.Head[..Math.Min(7, r.Head.Length)]}: {detail}");
            }
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        var masterHead = _git.CommitOf(ctx.Master, "HEAD")!;
        var hosts = new HashSet<string?>();
        foreach (var r in repoList.Append((Repo: "master", Path: ctx.Master, Head: masterHead)))
        {
            var remote = RemoteProblem(r.Repo, r.Path, branch, r.Head);
            if (remote is not null)
            {
                problems.Add(remote);
            }

            hosts.Add(_pr.HostOf(_git.Out(r.Path, ["remote", "get-url", "origin"]) ?? ""));
        }

        foreach (var host in hosts)
        {
            var signIn = _pr.SignInProblem(ctx.Master, host);
            if (signIn is not null)
            {
                problems.Add(signIn);
            }
        }

        return problems;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public Task<StepResult> ActAsync(EventContext ctx)
    {
        if (_apps.IsSingleRepo(ctx.Master))
        {
            return Task.FromResult(ActSingleRepo(ctx));
        }

        var record = RecordOf(ctx);
        var branch = record.Branch;
        var title = PullRequestTitle(ctx, record);
        var intro = $"Change `{ctx.Change}` of system `{ctx.System}`, built with hoshin. Its intent, spec, plan, build logs and review are in the master repository's pull request.";
        var opened = record.PullRequests.Where(p => p.Repo != "master").ToList();
        var pushed = new List<string>();

        StepResult Fail(string why) => new(
            [why, pushed.Count > 0 ? $"pushed so far: {string.Join(", ", pushed)}; running done again carries on from there" : "nothing was pushed"],
            new Dictionary<string, object?> { ["pushed"] = pushed, ["pullRequests"] = opened });

        string? Push(string repo, string path)
        {
            var r = _git.Run(path, ["push", "--quiet", "-u", "origin", $"refs/heads/{branch}:refs/heads/{branch}"]);
            if (!r.Ok)
            {
                var detail = (string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim();
                return $"pushing {branch} in {repo} failed (a pre-push hook, or the remote refused it): {detail}";
            }

            pushed.Add(repo);
            return null;
        }

        (PullRequestRecord? Pr, string? Err) OpenOrReuse(string repo, string path, string body)
        {
            var head = _git.CommitOf(path, $"refs/heads/{branch}")!;
            var baseBranch = _git.BaseBranch(path) ?? record.Base;
            var existing = _pr.OpenPullRequest(path, branch);
            if (existing is not null)
            {
                return (new PullRequestRecord(repo, existing.Url, existing.Number, head), null);
            }

            var created = _pr.OpenNew(path, new OpenPullRequestInput(baseBranch, branch, title, body));
            if (created.PullRequest is null)
            {
                return (null, $"opening the pull request in {repo} failed: {created.Error}");
            }

            return (new PullRequestRecord(repo, created.PullRequest.Url, created.PullRequest.Number, head), null);
        }

        string? Save(string header)
        {
            var file = ChangeRecordStore.Write(ctx.Folder, record with { PullRequests = opened });
            var r = _git.CommitPaths(ctx.Master, [file], [header, Committer.Trailer]);
            return r.Ok || NothingToCommit.IsMatch(r.Output) ? null : $"the commit \"{header}\" failed: {r.Error}";
        }

        foreach (var r in Touched(ctx, branch))
        {
            var pushErr = Push(r.Repo, r.Path);
            if (pushErr is not null)
            {
                return Task.FromResult(Fail(pushErr));
            }

            var (pr, prErr) = OpenOrReuse(r.Repo, r.Path, intro);
            if (prErr is not null)
            {
                return Task.FromResult(Fail(prErr));
            }

            var idx = opened.FindIndex(p => p.Repo == r.Repo);
            if (idx >= 0)
            {
                opened[idx] = pr!;
            }
            else
            {
                opened.Add(pr!);
            }
        }

        var header = $"docs({record.Scope}): record pull requests {ctx.Change}";
        var saved = Save(header);
        if (saved is not null)
        {
            return Task.FromResult(Fail(saved));
        }

        var masterPushErr = Push("master", ctx.Master);
        if (masterPushErr is not null)
        {
            return Task.FromResult(Fail(masterPushErr));
        }

        var list = string.Join('\n', opened.Select(p => $"- {p.Repo}: {p.Url}"));
        var masterBody = $"Change `{ctx.Change}` of system `{ctx.System}`: its intent, spec, plan, build logs and review, built with hoshin.{(list.Length > 0 ? $"\n\nThe change's pull requests:\n{list}" : "")}";
        var (masterPr, masterPrErr) = OpenOrReuse("master", ctx.Master, masterBody);
        if (masterPrErr is not null)
        {
            return Task.FromResult(Fail(masterPrErr));
        }

        opened.Add(masterPr!);
        var savedAgain = Save(header);
        if (savedAgain is not null)
        {
            return Task.FromResult(Fail(savedAgain));
        }

        var finalPushErr = Push("master", ctx.Master);
        if (finalPushErr is not null)
        {
            return Task.FromResult(Fail(finalPushErr));
        }

        ctx.Head = _git.CommitOf(ctx.Master, "HEAD");
        return Task.FromResult(new StepResult([], new Dictionary<string, object?>
        {
            ["pushed"] = pushed.Distinct(StringComparer.Ordinal).ToList(),
            ["pullRequests"] = opened,
        }));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private async Task<List<string>> CheckSingleRepoAsync(EventContext ctx)
    {
        var record = RecordOf(ctx);
        var branch = record.Branch;
        var planApps = _progress.PlanRepos(ctx.Folder);
        if (planApps.Count == 0)
        {
            return [$"{ctx.Change} has no plan that reads cleanly, so there is nothing to declare done"];
        }

        var problems = new List<string>();
        var cur = _git.CurrentBranch(ctx.Master);
        if (cur != branch)
        {
            problems.Add($"the change's copy {ctx.Master} is on {cur ?? "a detached HEAD"}, not on {branch}");
        }

        var dirty = _git.DirtyPaths(ctx.Master);
        if (dirty.Count > 0)
        {
            problems.Add($"the change's copy has uncommitted files: {string.Join(", ", dirty)}");
        }

        foreach (var app in planApps)
        {
            var missing = _progress.UnloggedTasks(ctx.Folder, app) ?? [];
            if (missing.Count > 0)
            {
                problems.Add($"{app} still has tasks without a build log entry: {string.Join(", ", missing)}");
            }
        }

        var baseBranch = _git.BaseBranch(ctx.Master);
        if (baseBranch is null)
        {
            problems.Add($"the change's copy {ctx.Master} has no base branch to compare {branch} with");
        }

        if (problems.Count > 0 || baseBranch is null)
        {
            return problems;
        }

        var branchRef = $"refs/heads/{branch}";
        var folders = _apps.AppFolders(ctx.Master, ctx.System);
        var touched = _apps.TouchedApps(ctx.Master, ctx.System, ctx.Master, baseBranch, branchRef);
        string HeadOfApp(string app) => _apps.AppHead(ctx.Master, branchRef, folders[app], baseBranch);

        var broken = ScopeRules.CommitRuleProblems(_git.CommitsBeyondBase(ctx.Master, branchRef, baseBranch), ScopeRules.ChangeScope(ctx.Master, ctx.System, ctx.Folder));
        if (broken.Count > 0)
        {
            problems.Add($"commits on {branch} break the commit rules: {string.Join("; ", broken)}");
        }

        var latest = LatestReview(ctx.Folder);
        if (latest is null)
        {
            problems.Add($"{ctx.Change} has no review yet: run the hoshin review skill first");
        }
        else
        {
            var blocking = BlockingFindings(latest);
            if (blocking.Count > 0)
            {
                problems.Add($"the latest review (review {latest.Number}) has unresolved high findings: {string.Join("; ", blocking)}. Fix them and review again; a later review that no longer finds them clears them");
            }

            foreach (var app in touched)
            {
                latest.Commits.TryGetValue(app, out var at);
                var head = HeadOfApp(app);
                if (string.IsNullOrEmpty(at) || !head.StartsWith(at, StringComparison.Ordinal))
                {
                    problems.Add($"the latest review covered {app} at {at ?? "no commit"}, but its branch is now at {head[..Math.Min(7, head.Length)]}: run the review again");
                }
            }
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        foreach (var app in touched)
        {
            var head = HeadOfApp(app);
            var appPath = _apps.RepoPathOf(ctx.Master, ctx.Workspace, ctx.System, app);
            var subCtx = ctx.CloneFor("build.finish", app, appPath, ctx.Master, branch, baseBranch, head);
            var checks = await _runEvent!(subCtx, null).ConfigureAwait(false);
            if (checks.Outcome != "passed")
            {
                var detail = string.Join('\n', checks.Problems.Append(checks.Failed?.Output ?? "").Where(s => s.Length > 0));
                problems.Add($"{app}'s checks (build.finish) did not pass at {head[..Math.Min(7, head.Length)]}: {detail}");
            }
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        var remote = RemoteProblem("master", ctx.Master, branch, _git.CommitOf(ctx.Master, "HEAD")!);
        if (remote is not null)
        {
            problems.Add(remote);
        }

        var signIn = _pr.SignInProblem(ctx.Master, _pr.HostOf(_git.Out(ctx.Master, ["remote", "get-url", "origin"]) ?? ""));
        if (signIn is not null)
        {
            problems.Add(signIn);
        }

        return problems;
    }

    private StepResult ActSingleRepo(EventContext ctx)
    {
        var record = RecordOf(ctx);
        var branch = record.Branch;
        var baseBranch = _git.BaseBranch(ctx.Master) ?? record.Base;
        var touchedNames = _apps.TouchedApps(ctx.Master, ctx.System, ctx.Master, baseBranch, $"refs/heads/{branch}");
        var untouched = _progress.PlanRepos(ctx.Folder).Where(a => !touchedNames.Contains(a)).ToList();
        var opened = new List<PullRequestRecord>();
        var pushed = new List<string>();

        StepResult Fail(string why) => new(
            [why, pushed.Count > 0 ? $"pushed so far: {string.Join(", ", pushed)}; running done again carries on from there" : "nothing was pushed"],
            new Dictionary<string, object?> { ["pushed"] = pushed, ["pullRequests"] = opened, ["untouched"] = untouched });

        string? Push()
        {
            var r = _git.Run(ctx.Master, ["push", "--quiet", "-u", "origin", $"refs/heads/{branch}:refs/heads/{branch}"]);
            if (!r.Ok)
            {
                var detail = (string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim();
                return $"pushing {branch} in master failed (a pre-push hook, or the remote refused it): {detail}";
            }

            if (!pushed.Contains("master"))
            {
                pushed.Add("master");
            }

            return null;
        }

        var header = $"docs({record.Scope}): record pull requests {ctx.Change}";
        var pushErr = Push();
        if (pushErr is not null)
        {
            return Fail(pushErr);
        }

        var names = string.Join(", ", touchedNames.Select(a => $"`{a}`"));
        var body = $"Change `{ctx.Change}` of system `{ctx.System}`, built with hoshin. Its intent, spec, plan, build logs and review are in this pull request, under `intent/{ctx.System}/{ctx.Change}/`.\n\nApps touched: {(names.Length > 0 ? names : "none")}.";
        var pushedHead = _git.CommitOf(ctx.Master, $"refs/heads/{branch}")!;
        var existing = _pr.OpenPullRequest(ctx.Master, branch);
        PullRequestRef? prRef;
        if (existing is not null)
        {
            prRef = existing;
        }
        else
        {
            var created = _pr.OpenNew(ctx.Master, new OpenPullRequestInput(baseBranch, branch, PullRequestTitle(ctx, record), body));
            if (created.PullRequest is null)
            {
                return Fail($"opening the pull request in master failed: {created.Error}");
            }

            prRef = created.PullRequest;
        }

        opened.Add(new PullRequestRecord("master", prRef.Url, prRef.Number, pushedHead));
        var file = ChangeRecordStore.Write(ctx.Folder, record with { PullRequests = opened });
        var commitRes = _git.CommitPaths(ctx.Master, [file], [header, Committer.Trailer]);
        if (!commitRes.Ok && !NothingToCommit.IsMatch(commitRes.Output))
        {
            return Fail($"the commit \"{header}\" failed: {commitRes.Error}");
        }

        var pushAgain = Push();
        if (pushAgain is not null)
        {
            return Fail(pushAgain);
        }

        ctx.Head = _git.CommitOf(ctx.Master, "HEAD");
        return new StepResult([], new Dictionary<string, object?>
        {
            ["pushed"] = pushed,
            ["pullRequests"] = opened,
            ["untouched"] = untouched,
        });
    }

    private List<(string Repo, string Path, string Head)> Touched(EventContext ctx, string branch)
    {
        var result = new List<(string Repo, string Path, string Head)>();
        foreach (var repo in _progress.PlanRepos(ctx.Folder).Where(r => r != "master"))
        {
            var path = Path.Combine(ctx.Workspace, repo);
            var baseBranch = _git.BaseBranch(path);
            var head = _git.CommitOf(path, $"refs/heads/{branch}");
            if (baseBranch is not null && head is not null && _git.CommitsBeyondBase(path, $"refs/heads/{branch}", baseBranch).Count > 0)
            {
                result.Add((repo, path, head));
            }
        }

        return result;
    }

    private ParsedReviewSection? LatestReview(string folder)
    {
        var file = Path.Combine(folder, "build", "review.md");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var res = _review.Read(File.ReadAllText(file), file);
            return res.Document.Reviews.LastOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static List<string> BlockingFindings(ParsedReviewSection review) =>
        review.Findings
            .Where(f => BlockingSeverities.Contains(f.Severity))
            .Select(f =>
            {
                var firstLine = f.Text.Trim().Split('\n').FirstOrDefault() ?? "";
                var repoPrefix = string.IsNullOrEmpty(f.Repo) ? "" : $"{f.Repo}, ";
                return $"{f.Id} ({repoPrefix}{f.File}:{f.Line}): {firstLine}".TrimEnd();
            })
            .ToList();

    private static string IntentTitle(string folder)
    {
        var file = Path.Combine(folder, "intent.md");
        if (!File.Exists(file))
        {
            return "";
        }

        return FrontMatter.TryGetScalar(File.ReadAllText(file), file, "title") ?? "";
    }

    private string PullRequestTitle(EventContext ctx, ChangeRecord record)
    {
        var title = IntentTitle(ctx.Folder);
        if (string.IsNullOrEmpty(title))
        {
            title = ctx.Change;
        }

        var revision = ctx.Base is null ? null : _git.BaseRevision(ctx.Master, ctx.Base);
        var marker = revision is null ? null : _git.ShowFile(ctx.Master, revision, MarkerConstants.FileName);
        if (marker is null)
        {
            return title;
        }

        var naming = NamingService.NamingFromText(marker, ctx.System, ".hoshin.json on the base branch");
        if (!naming.Declared || string.IsNullOrEmpty(naming.Setting?.PullRequestTitle))
        {
            return title;
        }

        return NamingService.RenderNaming(naming.Setting, new NamingValues(record.Type, record.Scope, ctx.Change, title)).PullRequestTitle ?? title;
    }

    private static ChangeRecord RecordOf(EventContext ctx)
    {
        var found = ChangeRecordStore.Read(ctx.Folder);
        if (found is not null)
        {
            return found;
        }

        return new ChangeRecord(
            1,
            ChangeRecordStore.BranchOf(ctx.Folder),
            "feat",
            ScopeRules.ChangeScope(ctx.Master, ctx.System, ctx.Folder),
            ctx.Base ?? "main",
            []);
    }

    private string? RemoteProblem(string repo, string path, string branch, string head)
    {
        var url = _git.Out(path, ["remote", "get-url", "origin"]);
        if (string.IsNullOrEmpty(url))
        {
            return $"{repo} has no remote named origin to push to";
        }

        var listed = _git.Run(path, ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"]);
        if (!listed.Ok)
        {
            return $"{repo}'s remote cannot be reached: {listed.Stderr.Trim()}";
        }

        var remoteHead = listed.Stdout.Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(remoteHead))
        {
            return null;
        }

        if (_git.CommitOf(path, remoteHead) is not null && _git.IsAncestor(path, remoteHead, head))
        {
            return null;
        }

        return $"{branch} is already taken on {repo}'s remote by work that is not on this machine's branch: pick a new name with change rename, and nothing is pushed";
    }
}
