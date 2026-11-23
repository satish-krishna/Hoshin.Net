using System.Text.RegularExpressions;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Events.Steps;

/// <summary><c>change.start</c>: creates the change branch, worktree copy, and initial <c>change.jsonc</c> commit.</summary>
public sealed class ChangeStartStep(
    IGitClient git,
    AppService apps,
    BranchChecker branchChecker,
    ChangeRecordStore changeRecords) : IEventStep
{
    private static readonly Regex ChangeNameRegex = new(@"^\d{4}-\d{2}-\d{2}-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly AppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));
    private readonly BranchChecker _branches = branchChecker ?? throw new ArgumentNullException(nameof(branchChecker));
    private readonly ChangeRecordStore _changeRecords = changeRecords ?? throw new ArgumentNullException(nameof(changeRecords));

    public string EventName => "change.start";
    public bool ReusePassed => false;

    public static string CopyOf(EventContext ctx) => Path.Combine(ChangeLocator.CopiesFolder(ctx.Workspace), ctx.Change);

    public Task<List<string>> CheckAsync(EventContext ctx)
    {
        var problems = new List<string>();
        if (!ChangeNameRegex.IsMatch(ctx.Change))
        {
            problems.Add($"{ctx.Change} is not a change name: YYYY-MM-DD-<slug>, lowercase letters, digits and hyphens");
        }

        ctx.Extra.TryGetValue("type", out var type);
        if (type is null || !ChangeRecordStore.CommitTypes.Contains(type))
        {
            problems.Add($"the work type must be one of {string.Join(", ", ChangeRecordStore.CommitTypes)}");
        }

        ctx.Extra.TryGetValue("scope", out var scope);
        problems.AddRange(NamingService.CheckScope(scope ?? "").Select(p => $"the scope: {p}"));

        if (ctx.Base is null || _git.BaseRevision(ctx.Master, ctx.Base) is null)
        {
            problems.Add("the master repository has no base branch (the remote's default, main or master) on this machine");
        }

        if (problems.Count > 0)
        {
            return Task.FromResult(problems);
        }

        var (exists, record) = ExistingCopy(ctx);
        if (exists && record is not null)
        {
            return Task.FromResult(new List<string>());
        }

        if (exists)
        {
            return Task.FromResult(new List<string> { $"{CopyOf(ctx)} exists but holds no {ChangeRecordStore.RecordFileName} for {ctx.Change}; move it away, or finish the change it belongs to" });
        }

        if (Directory.Exists(Path.Combine(ctx.Master, "intent", ctx.System, ctx.Change)))
        {
            return Task.FromResult(new List<string> { $"{ctx.Change} already exists in {ctx.Master}: it was started before changes had their own copy, and it keeps working where it is" });
        }

        var (name, namingProblems) = ResolveBranchName(ctx);
        if (name is null)
        {
            return Task.FromResult(namingProblems);
        }

        var invalid = NamingService.CheckBranchName(name);
        if (invalid.Count > 0)
        {
            return Task.FromResult(invalid.Select(p => $"{name}: {p}").ToList());
        }

        var allRepos = new List<RepositoryRef> { new("master", ctx.Master) };
        allRepos.AddRange(_apps.ProductRepositories(ctx.Master, ctx.Workspace, ctx.Repos));
        return Task.FromResult(_branches.BranchClashes(name, allRepos));
    }

    public Task<StepResult> ActAsync(EventContext ctx)
    {
        var copy = CopyOf(ctx);
        var folder = Path.Combine(copy, "intent", ctx.System, ctx.Change);
        var (_, existing) = ExistingCopy(ctx);

        void Point(string br)
        {
            ctx.Master = copy;
            ctx.RepoPath = copy;
            ctx.Folder = folder;
            ctx.Branch = br;
            ctx.Head = _git.CommitOf(copy, "HEAD");
        }

        if (existing is not null)
        {
            Point(existing.Branch);
            return Task.FromResult(new StepResult([], new Dictionary<string, object?>
            {
                ["branch"] = existing.Branch,
                ["type"] = existing.Type,
                ["base"] = existing.Base,
                ["copy"] = copy,
                ["created"] = false,
            }));
        }

        var (branch, _) = ResolveBranchName(ctx);
        var branchName = branch!;
        var baseBranch = ctx.Base!;

        if (!_git.HasLocalBranch(ctx.Master, branchName))
        {
            var made = _git.Run(ctx.Master, ["branch", branchName, _git.BaseRevision(ctx.Master, baseBranch)!]);
            if (!made.Ok)
            {
                return Task.FromResult(new StepResult([$"git could not create {branchName}: {made.Stderr.Trim()}"]));
            }
        }

        Directory.CreateDirectory(ChangeLocator.CopiesFolder(ctx.Workspace));
        var added = _git.Run(ctx.Master, ["worktree", "add", "--quiet", copy, branchName]);
        if (!added.Ok)
        {
            return Task.FromResult(new StepResult([$"git could not make the change's copy in {copy}: {added.Stderr.Trim()}"]));
        }

        Directory.CreateDirectory(folder);
        var type = ctx.Extra["type"];
        var scope = ctx.Extra["scope"];
        var file = _changeRecords.Write(folder, new ChangeRecord(1, branchName, type, scope, baseBranch, []));
        var header = $"docs({scope}): start change {ctx.Change}";
        var committed = _git.CommitPaths(copy, [file], [header, Committer.Trailer]);
        if (!committed.Ok)
        {
            return Task.FromResult(new StepResult(
                [$"the commit \"{header}\" failed in {copy}: {committed.Error}"],
                new Dictionary<string, object?> { ["branch"] = branchName, ["copy"] = copy }));
        }

        Point(branchName);
        return Task.FromResult(new StepResult([], new Dictionary<string, object?>
        {
            ["branch"] = branchName,
            ["type"] = type,
            ["base"] = baseBranch,
            ["copy"] = copy,
            ["created"] = true,
        }));
    }

    private (string? Name, List<string> Problems) ResolveBranchName(EventContext ctx)
    {
        if (ctx.Extra.TryGetValue("branch", out var explicitBranch) && !string.IsNullOrEmpty(explicitBranch))
        {
            return (explicitBranch, []);
        }

        var revision = ctx.Base is null ? null : _git.BaseRevision(ctx.Master, ctx.Base);
        var marker = revision is null ? null : _git.ShowFile(ctx.Master, revision, MarkerConstants.FileName);
        var naming = marker is null
            ? new NamingRead(false, null)
            : NamingService.NamingFromText(marker, ctx.System, ".hoshin.json on the base branch");
        if (!naming.Declared || naming.Setting is null)
        {
            return ($"change/{ctx.Change}", []);
        }

        var problems = NamingService.CheckNaming(naming.Setting);
        if (problems.Count > 0)
        {
            return (null, problems.Select(p => $"the team naming on the base branch: {p}").ToList());
        }

        var values = new NamingValues(
            ctx.Extra.GetValueOrDefault("type", ""),
            ctx.Extra.GetValueOrDefault("scope", ""),
            ctx.Change,
            ctx.Extra.GetValueOrDefault("title"));
        return (NamingService.RenderNaming(naming.Setting, values).Branch, []);
    }

    private (bool Exists, ChangeRecord? Record) ExistingCopy(EventContext ctx)
    {
        var copy = CopyOf(ctx);
        var exists = Directory.Exists(copy);
        var record = exists ? _changeRecords.Read(Path.Combine(copy, "intent", ctx.System, ctx.Change)) : null;
        return (exists, record);
    }
}
