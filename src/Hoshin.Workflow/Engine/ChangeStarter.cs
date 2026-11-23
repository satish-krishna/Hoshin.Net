using System.Globalization;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Engine;

/// <summary>An open change available for selection by a hook invocation.</summary>
public sealed record OpenChangeOption(string Change, string MasterRoot, string ChangeFolder);

/// <summary>Result of starting a new change via <c>EventRunner</c> <c>change.start</c>.</summary>
public sealed record ChangeStartHookResult(
    string? CopyRoot,
    string? ChangeFolder,
    string? ChangeName,
    string? Branch,
    string? Error)
{
    public bool Ok => Error is null && !string.IsNullOrEmpty(CopyRoot) && !string.IsNullOrEmpty(ChangeFolder);
}

/// <summary>Lists open changes and starts new changes via <see cref="EventRunner"/> <c>change.start</c> from hook invocations.</summary>
public interface IChangeStarter
{
    bool HasWorkspaceMarker(WorkflowRepo repo);

    IReadOnlyList<OpenChangeOption> ListOpenChanges(WorkflowRepo repo);

    ChangeStartHookResult StartFromHook(string cwd, string skill, string? rawArgs);
}

/// <summary>Starts new changes through <c>EventRunner</c> (<c>ChangeStartStep</c>) and discovers open changes across main checkout and copies.</summary>
public sealed partial class ChangeStarter(
    IWorkspaceResolver resolver,
    MarkerReader markerReader,
    ChangeLocator changeLocator,
    ChangeListService changeList,
    ScopeRules scopeRules,
    EventRunner eventRunner,
    IGitWorktrees gitWorktrees,
    IGitBranches gitBranches,
    IGitQuery gitQuery,
    IFileSystem fileSystem,
    TimeProvider timeProvider) : IChangeStarter
{
    private readonly IWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly MarkerReader _markerReader = markerReader ?? throw new ArgumentNullException(nameof(markerReader));
    private readonly ChangeLocator _changeLocator = changeLocator ?? throw new ArgumentNullException(nameof(changeLocator));
    private readonly ChangeListService _changeList = changeList ?? throw new ArgumentNullException(nameof(changeList));
    private readonly ScopeRules _scopeRules = scopeRules ?? throw new ArgumentNullException(nameof(scopeRules));
    private readonly EventRunner _eventRunner = eventRunner ?? throw new ArgumentNullException(nameof(eventRunner));
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));
    private readonly IGitBranches _gitBranches = gitBranches ?? throw new ArgumentNullException(nameof(gitBranches));
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex FullChangeNameRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugCharsRegex();

    public bool HasWorkspaceMarker(WorkflowRepo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (_fs.FileExists(Path.Combine(repo.Root, MarkerConstants.FileName)))
        {
            return true;
        }

        var parent = Directory.GetParent(repo.Root)?.FullName;
        return parent is not null && _fs.FileExists(Path.Combine(parent, "master", MarkerConstants.FileName));
    }

    public IReadOnlyList<OpenChangeOption> ListOpenChanges(WorkflowRepo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (!HasWorkspaceMarker(repo))
        {
            return ChangeFolderResolver.ListIntentChanges(repo.Root, _fs)
                .Select(c => new OpenChangeOption(c.Change, repo.Root, c.Folder))
                .ToList();
        }

        var resolution = _resolver.Resolve(repo.Root, new ResolveOptions(null, null));
        if (resolution.Master is null)
        {
            return [];
        }

        var mainMaster = _gitWorktrees.MainCheckout(resolution.Master);
        var marker = _markerReader.ReadMarker(Path.Combine(mainMaster, MarkerConstants.FileName));
        var options = new List<OpenChangeOption>();
        foreach (var system in marker.Systems.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var listed = _changeList.List(repo.Root, new ResolveOptions(null, null), system);
            foreach (var item in listed.Changes.Where(c => c.Finished is null))
            {
                var folder = Path.Combine(item.Master, "intent", listed.System, item.Change);
                options.Add(new OpenChangeOption(item.Change, item.Master, folder));
            }
        }

        return options;
    }

    public ChangeStartHookResult StartFromHook(string cwd, string skill, string? rawArgs)
    {
        var resolution = _resolver.Resolve(cwd, new ResolveOptions(null, null));
        if (resolution.Master is null || resolution.Workspace is null)
        {
            return Fail(string.Join("; ", resolution.Problems));
        }

        var mainMaster = _gitWorktrees.MainCheckout(resolution.Master);
        var marker = _markerReader.ReadMarker(Path.Combine(mainMaster, MarkerConstants.FileName));
        if (marker.Systems.Count == 0)
        {
            return Fail("no systems are declared in .hoshin.json");
        }

        if (marker.Systems.Count > 1)
        {
            var names = string.Join(", ", marker.Systems.Keys.OrderBy(k => k, StringComparer.Ordinal));
            return Fail($"multiple systems exist in .hoshin.json ({names}); ask the engineer which system this change belongs to");
        }

        var system = marker.Systems.Keys.First();
        var changeName = DeriveChangeName(rawArgs, _time.GetUtcNow());
        if (changeName is null)
        {
            return Fail($"no change name was provided for `/{skill}`; ask the engineer for a short kebab-case change name (for example `add-login`)");
        }

        var workType = string.Equals(skill, "fix", StringComparison.OrdinalIgnoreCase) ? "fix" : "feat";
        var scope = _scopeRules.SystemScope(mainMaster, system);
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = workType,
            ["scope"] = scope,
        };
        if (!string.IsNullOrWhiteSpace(rawArgs))
        {
            extra["title"] = rawArgs.Trim();
        }

        var ctx = new EventContext
        {
            Event = "change.start",
            Workspace = resolution.Workspace,
            Master = mainMaster,
            System = system,
            Change = changeName,
            Folder = Path.Combine(ChangeLocator.CopiesFolder(resolution.Workspace), changeName, "intent", system, changeName),
            Repo = "master",
            RepoPath = mainMaster,
            Root = mainMaster,
            Branch = _gitBranches.CurrentBranch(mainMaster),
            Base = _gitBranches.BaseBranch(mainMaster),
            Head = _gitQuery.CommitOf(mainMaster, "HEAD"),
            Repos = _changeLocator.SystemRepos(mainMaster, resolution.Workspace, system),
            Extra = extra,
        };

        var outcome = _eventRunner.RunAsync(ctx).GetAwaiter().GetResult();
        if (outcome.Outcome != "passed")
        {
            var detail = WorkflowEventBridge.FormatOutcome(outcome);
            return Fail(string.IsNullOrWhiteSpace(detail) ? $"change.start {outcome.Outcome}" : detail);
        }

        return new ChangeStartHookResult(ctx.Master, ctx.Folder, changeName, ctx.Branch, null);
    }

    public static string? DeriveChangeName(string? rawArgs, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(rawArgs))
        {
            return null;
        }

        var trimmed = rawArgs.Trim();
        var firstToken = trimmed.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
        if (FullChangeNameRegex().IsMatch(firstToken))
        {
            return firstToken;
        }

        var slug = NonSlugCharsRegex().Replace(trimmed.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            return null;
        }

        if (slug.Length > 48)
        {
            slug = slug[..48].TrimEnd('-');
        }

        var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{date}-{slug}";
    }

    private static ChangeStartHookResult Fail(string error) => new(null, null, null, null, error);
}
