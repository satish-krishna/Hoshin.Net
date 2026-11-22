using System.Text.Json;
using System.Text.Json.Serialization;
using Hoshin.Workflow.Baseline;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Events.Steps;

public sealed record ActiveBuildRecord(
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("change")] string Change,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("master")] string Master,
    [property: JsonPropertyName("workspace")] string Workspace);

/// <summary><c>build.start</c>: switches product repo to change branch, writes <c>active-build.json</c>, and takes baseline.</summary>
public sealed class BuildStartStep(
    IGitClient? git = null,
    AppService? apps = null,
    EventContextFactory? contextFactory = null,
    LedgerStore? ledgerStore = null,
    BaselineTaker? baselineTaker = null,
    BaselineApprovals? approvals = null) : IEventStep
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;
    private readonly AppService _apps = apps ?? new AppService(git);
    private readonly EventContextFactory _contextFactory = contextFactory ?? new EventContextFactory(git);
    private readonly LedgerStore _ledger = ledgerStore ?? new LedgerStore(git);
    private readonly BaselineTaker _baselineTaker = baselineTaker ?? new BaselineTaker(git);
    private readonly BaselineApprovals _approvals = approvals ?? new BaselineApprovals(ledgerStore);

    public string EventName => "build.start";
    public bool ReusePassed => false;

    public string ActiveBuildFile(string root) => _git.GitPath(root, Path.Combine("hoshin", "active-build.json"));

    public ActiveBuildRecord? ReadActiveBuild(string root)
    {
        var file = ActiveBuildFile(root);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ActiveBuildRecord>(File.ReadAllText(file), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public Task<List<string>> CheckAsync(EventContext ctx)
    {
        var branch = ChangeRecordStore.BranchOf(ctx.Folder);
        if (_apps.IsSingleRepo(ctx.Master) && ctx.Repo != "master")
        {
            var stuck = _baselineTaker.RecoverInterrupted(ctx.Root);
            if (stuck is not null)
            {
                return Task.FromResult(new List<string> { stuck });
            }

            var cur = _git.CurrentBranch(ctx.Root);
            if (cur != branch)
            {
                return Task.FromResult(new List<string> { $"{ctx.Repo} is built in the change's copy, on {branch}; {ctx.Root} is on {cur ?? "a detached HEAD"}" });
            }

            var other = OtherAppRefusal(ctx);
            return Task.FromResult(other is null ? new List<string>() : new List<string> { other });
        }

        if (ctx.Repo == "master")
        {
            var cur = _git.CurrentBranch(ctx.Master);
            return Task.FromResult(cur == branch
                ? new List<string>()
                : new List<string> { $"the master repository is built in the change's copy, on {branch}; {ctx.Master} is on {cur ?? "a detached HEAD"}" });
        }

        if (ctx.Base is null || _git.BaseRevision(ctx.RepoPath, ctx.Base) is null)
        {
            return Task.FromResult(new List<string> { $"{ctx.Repo} has no base branch (the remote's default, main or master) on this machine" });
        }

        var dirty = ctx.Branch == branch ? [] : OwnFiles.UncommittedFiles(_git, ctx.RepoPath);
        if (dirty.Count > 0)
        {
            return Task.FromResult(new List<string> { $"{ctx.Repo} has uncommitted files: {string.Join(", ", dirty)}. Commit or put them away first; nothing was switched." });
        }

        var elsewhere = _git.CheckedOutIn(ctx.RepoPath, branch);
        if (elsewhere is not null && !PathHelpers.PathsEqual(elsewhere, ctx.RepoPath) && ctx.Branch != branch)
        {
            return Task.FromResult(new List<string> { $"{branch} is checked out in another worktree of {ctx.Repo}, {elsewhere}" });
        }

        if (ctx.Branch != ctx.Base && ctx.Branch != branch)
        {
            var why = HandOffRefusal(ctx);
            if (why is not null)
            {
                return Task.FromResult(new List<string> { $"{ctx.Repo} is on {ctx.Branch ?? "a detached HEAD"}, not on its base branch {ctx.Base} or the change's branch {branch}{why}" });
            }
        }

        var from = StartingPoint(ctx, branch);
        if (from is not null)
        {
            var strangers = ScopeRules.CommitRuleProblems(
                _git.CommitsBeyondBase(ctx.RepoPath, from, ctx.Base),
                ScopeRules.ChangeScope(ctx.Master, ctx.System, ctx.Folder));
            if (strangers.Count > 0)
            {
                return Task.FromResult(new List<string> { $"{ctx.Repo} already has a branch {branch} with work that is not this change's, so the build touches nothing. Pick a new branch name for the change (change rename). The commits: {string.Join("; ", strangers)}" });
            }
        }

        return Task.FromResult(new List<string>());
    }

    public Task<StepResult> ActAsync(EventContext ctx)
    {
        var branch = ChangeRecordStore.BranchOf(ctx.Folder);
        var from = ctx.Branch;
        var created = false;

        if (ctx.Repo != "master" && !_apps.IsSingleRepo(ctx.Master) && ctx.Branch != branch)
        {
            var start = StartingPoint(ctx, branch);
            string[] args = start is null
                ? ["checkout", "--quiet", "-b", branch, _git.BaseRevision(ctx.RepoPath, ctx.Base!)!]
                : start.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? ["checkout", "--quiet", branch]
                    : ["checkout", "--quiet", "-b", branch, "--track", start];
            var r = _git.Run(ctx.RepoPath, args);
            if (!r.Ok)
            {
                return Task.FromResult(new StepResult([$"git could not put {ctx.Repo} on {branch}: {r.Stderr.Trim()}"]));
            }

            created = start is null;
        }

        ctx.Branch = _git.CurrentBranch(ctx.RepoPath);
        ctx.Head = _contextFactory.HeadOf(ctx);

        var active = new ActiveBuildRecord(ctx.System, ctx.Change, ctx.Repo, ctx.Master, ctx.Workspace);
        var file = ActiveBuildFile(ctx.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(active, JsonOptions) + "\n");

        return Task.FromResult(new StepResult([], new Dictionary<string, object?>
        {
            ["branch"] = branch,
            ["created"] = created,
            ["uncommitted"] = _contextFactory.UncommittedOf(ctx),
            ["from"] = from,
        }));
    }

    public async Task<StepResult> FinishAsync(EventContext ctx, HookConfig config)
    {
        var stored = _baselineTaker.Stored(ctx, config);
        if (stored.Base is not null && stored.Entry is null && _contextFactory.UncommittedOf(ctx).Count > 0)
        {
            return new StepResult([], new Dictionary<string, object?>
            {
                ["baseline"] = new Dictionary<string, object?>
                {
                    ["base"] = stored.Base,
                    ["deferred"] = $"{ctx.Repo} has uncommitted files, so build.finish takes the baseline",
                },
            });
        }

        var taken = await _baselineTaker.TakeAsync(ctx, config).ConfigureAwait(false);
        if (taken.Problems.Count > 0)
        {
            var failed = taken.Fault is null ? null : new FailedHookInfo(taken.Fault.Id, "stop", taken.Fault.Output);
            return new StepResult(taken.Problems, Failed: failed, Outcome: taken.Outcome == "could-not-run" ? "could-not-run" : "failed");
        }

        if (taken.Entry is null || taken.Base is null)
        {
            return StepResult.Empty;
        }

        var baseCommit = taken.Base;
        var approved = BaselineApprovals.ApprovedHooks(_approvals.LedgerOf(ctx), baseCommit);
        var failing = taken.Entry.Hooks
            .Where(h => h.Outcome == "failed")
            .Select(h => new Dictionary<string, object?> { ["id"] = h.Id, ["ids"] = h.Ids })
            .ToList();
        var approvedFailing = taken.Entry.Hooks
            .Where(h => h.Outcome == "failed" && approved.Contains(h.Id))
            .Select(h => h.Id)
            .ToList();

        var baseline = new Dictionary<string, object?>
        {
            ["base"] = baseCommit,
            ["reused"] = taken.Reused,
            ["failing"] = failing,
            ["approved"] = approvedFailing,
        };

        var waiting = BaselineApprovals.NeedsApproval(taken.Entry, approved);
        if (waiting.Count == 0)
        {
            return new StepResult([], new Dictionary<string, object?> { ["baseline"] = baseline });
        }

        var first = taken.Entry.Hooks.FirstOrDefault(h => h.Id == waiting[0]);
        return new StepResult(
            waiting.Select(id => _approvals.ApprovalProblem(ctx, baseCommit, id)).ToList(),
            new Dictionary<string, object?> { ["baseline"] = baseline },
            new FailedHookInfo(waiting[0], "stop", first?.Output ?? ""));
    }

    private string? StartingPoint(EventContext ctx, string branch)
    {
        if (_git.HasLocalBranch(ctx.RepoPath, branch))
        {
            return $"refs/heads/{branch}";
        }

        if (_git.HasRemoteBranch(ctx.RepoPath, branch))
        {
            return $"refs/remotes/origin/{branch}";
        }

        return null;
    }

    private string? ChangeOnBranch(EventContext ctx, string branch)
    {
        var holders = new List<string> { _git.MainCheckout(ctx.Master) };
        var copiesDir = ChangeLocator.CopiesFolder(ctx.Workspace);
        if (Directory.Exists(copiesDir))
        {
            holders.AddRange(Directory.GetDirectories(copiesDir));
        }

        foreach (var holder in holders)
        {
            var systemDir = Path.Combine(holder, "intent", ctx.System);
            if (!Directory.Exists(systemDir))
            {
                continue;
            }

            foreach (var dir in Directory.GetDirectories(systemDir))
            {
                var name = Path.GetFileName(dir);
                if (name == ctx.Change || !File.Exists(Path.Combine(dir, "intent.md")))
                {
                    continue;
                }

                if (ChangeRecordStore.BranchOf(dir) == branch)
                {
                    return name;
                }
            }
        }

        return null;
    }

    private string? HandOffRefusal(EventContext ctx)
    {
        if (ctx.Branch is null)
        {
            return "";
        }

        var owner = ChangeOnBranch(ctx, ctx.Branch);
        if (owner is null)
        {
            return "";
        }

        var active = ReadActiveBuild(ctx.Root);
        if (active is null || active.Change != owner || active.System != ctx.System)
        {
            return null;
        }

        var finished = ctx.Head is null
            ? null
            : LedgerStore.Latest(_ledger.Read(_ledger.LedgerFolder(ctx.Master, active.System, active.Change), ctx.Repo), "build.finish", ctx.Head);
        return finished?.Outcome == "passed"
            ? null
            : $"; it is the branch of {owner}, whose build is active here and has not finished at its head (its build.finish has not passed there)";
    }

    private string? OtherAppRefusal(EventContext ctx)
    {
        var active = ReadActiveBuild(ctx.Root);
        if (active is null || active.System != ctx.System || active.Change != ctx.Change || active.Repo == ctx.Repo)
        {
            return null;
        }

        if (!_apps.AppFolders(ctx.Master, ctx.System).TryGetValue(active.Repo, out var folder) || ctx.Base is null)
        {
            return null;
        }

        string head;
        try
        {
            head = _apps.AppHead(ctx.Root, "HEAD", folder, ctx.Base);
        }
        catch (CouldNotRunException)
        {
            return $"`{active.Repo}` is being built in this copy, and its head cannot be read; build one app at a time";
        }

        var finished = LedgerStore.Latest(_ledger.Read(_ledger.LedgerFolder(ctx.Master, ctx.System, ctx.Change), active.Repo), "build.finish", head);
        return finished?.Outcome == "passed"
            ? null
            : $"`{active.Repo}` is being built in this copy; build one app at a time";
    }
}
