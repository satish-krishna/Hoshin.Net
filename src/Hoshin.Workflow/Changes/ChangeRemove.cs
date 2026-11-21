using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

public sealed record RemoveInput(string Master, string Workspace, string System, string Change, IReadOnlyList<string> Repos, string Copy, bool Abandon = false);

public sealed record RemoveOutcome(List<string> Problems, List<string> Removed, List<string> Kept);

/// <summary>Removes a change's worktree copy and merged (or abandoned) local branches.</summary>
public sealed class ChangeRemoveService(IGitClient? git = null, AppService? apps = null)
{
    public static ChangeRemoveService Default { get; } = new();

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;
    private readonly AppService _apps = apps ?? new AppService(git);

    public RemoveOutcome Remove(RemoveInput input)
    {
        var record = ChangeRecordStore.Read(Path.Combine(input.Copy, "intent", input.System, input.Change));
        if (!Directory.Exists(input.Copy) || record is null)
        {
            return new RemoveOutcome([$"{input.Change} has no copy with a {ChangeRecordStore.RecordFileName} at {input.Copy}"], [], []);
        }

        var main = _git.MainCheckout(input.Copy);
        var products = _apps.ProductRepositories(input.Master, input.Workspace, input.Repos);
        var isRejected = input.Abandon && IsRejected(input);
        if (input.Abandon)
        {
            var workProblems = WorkDone(input, record, isRejected);
            if (workProblems.Count > 0)
            {
                return new RemoveOutcome(workProblems, [], []);
            }
        }

        var gone = _git.Run(main, ["worktree", "remove", input.Copy]);
        if (!gone.Ok)
        {
            var dirty = Directory.Exists(input.Copy) ? _git.DirtyPaths(input.Copy) : [];
            var msg = dirty.Count > 0
                ? $"the change's copy has uncommitted files: {string.Join(", ", dirty)}"
                : $"git could not remove the copy: {gone.Stderr.Trim()}";
            return new RemoveOutcome([msg], [], []);
        }

        var removed = new List<string> { "copy" };
        var kept = new List<string>();
        var targets = new List<(string Repo, string Path)> { ("master", main) };
        targets.AddRange(products.Select(r => (r.Repo, r.Path)));

        foreach (var (repo, path) in targets)
        {
            if (!Directory.Exists(path) || !_git.HasLocalBranch(path, record.Branch))
            {
                continue;
            }

            if (repo == "master" && isRejected)
            {
                kept.Add($"master: {record.Branch} (it records the rejected intent)");
                continue;
            }

            var r = _git.Run(path, ["branch", "--quiet", input.Abandon ? "-D" : "-d", record.Branch]);
            if (r.Ok)
            {
                removed.Add($"{repo}: {record.Branch}");
            }
            else
            {
                var firstLine = r.Stderr.Trim().Replace("\r\n", "\n").Split('\n')[0];
                kept.Add($"{repo}: {record.Branch} ({firstLine})");
            }
        }

        return new RemoveOutcome([], removed, kept);
    }

    private bool IsRejected(RemoveInput input)
    {
        var path = $"intent/{input.System}/{input.Change}/intent.md";
        var text = _git.ShowFile(input.Copy, "HEAD", path);
        return text is not null && FixStateService.StatusOf(text, path) == "rejected";
    }

    private List<string> WorkDone(RemoveInput input, ChangeRecord record, bool isRejected)
    {
        var problems = new List<string>();
        foreach (var pr in record.PullRequests)
        {
            problems.Add($"{ChangeRecordStore.RecordFileName} records a pull request: {pr.Url}; a change with a pull request is not abandoned, close the pull request and remove the change once it is done");
        }

        var own = $"docs({record.Scope}): start change {input.Change}";
        var masterCommits = _git.CommitsBeyondBase(input.Copy, record.Branch, record.Base)
            .Where(c => !(c.Subject == own && c.GeneratedByHoshin))
            .ToList();
        var whose = _apps.IsSingleRepo(input.Master) ? "the repository's" : "the master's";
        if (masterCommits.Count > 0 && !isRejected)
        {
            problems.Add($"{whose} change branch {record.Branch} has commits beyond {record.Base}: {string.Join("; ", masterCommits.Select(c => c.Subject))}");
        }

        foreach (var r in _apps.ProductRepositories(input.Master, input.Workspace, input.Repos))
        {
            if (!_git.HasLocalBranch(r.Path, record.Branch))
            {
                continue;
            }

            var baseBranch = _git.BaseBranch(r.Path);
            if (baseBranch is null)
            {
                problems.Add($"{r.Repo} has no base branch (the remote's default, main or master) to compare {record.Branch} with");
                continue;
            }

            var commits = _git.CommitsBeyondBase(r.Path, record.Branch, baseBranch);
            if (commits.Count > 0)
            {
                problems.Add($"{r.Repo}'s change branch {record.Branch} has commits beyond {baseBranch}: {string.Join("; ", commits.Select(c => c.Subject))}");
            }

            var where = _git.CheckedOutIn(r.Path, record.Branch);
            if (where is not null)
            {
                problems.Add($"{r.Repo} has {record.Branch} checked out in {where}; switch it to {baseBranch} first");
            }
        }

        var dirty = _git.DirtyPaths(input.Copy);
        if (dirty.Count > 0)
        {
            problems.Add($"the change's copy has uncommitted files: {string.Join(", ", dirty)}");
        }

        return problems;
    }
}
