using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

public sealed record RenameInput(string Master, string Workspace, string System, string Change, IReadOnlyList<string> Repos, string To, string Scope);

public sealed record RenameOutcome(List<string> Problems, List<string> Renamed, string? From);

/// <summary>Renames a change's branch across master and product repositories with rollback on failure.</summary>
public sealed class ChangeRenameService(IGitClient git, AppService apps, BranchChecker branches, ChangeRecordStore changeRecords)
{
    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly AppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));
    private readonly BranchChecker _branches = branches ?? throw new ArgumentNullException(nameof(branches));
    private readonly ChangeRecordStore _changeRecords = changeRecords ?? throw new ArgumentNullException(nameof(changeRecords));

    public RenameOutcome Rename(RenameInput input)
    {
        var folder = Path.Combine(input.Master, "intent", input.System, input.Change);
        var record = _changeRecords.Read(folder);
        if (record is null)
        {
            return new RenameOutcome(
                [$"{input.Change} has no {ChangeRecordStore.RecordFileName} in {input.Master}; only a change started with change start can be renamed"],
                [],
                null);
        }

        var from = record.Branch;
        var problems = new List<string>();
        if (input.To == from)
        {
            problems.Add($"the change's branch is already {from}");
        }

        problems.AddRange(NamingService.CheckBranchName(input.To).Select(p => $"{input.To}: {p}"));
        var products = _apps.ProductRepositories(input.Master, input.Workspace, input.Repos);
        var holders = products.Where(r => _git.HasLocalBranch(r.Path, from)).ToList();
        var allRepos = new List<RepositoryRef> { new("master", input.Master) };
        allRepos.AddRange(products);
        problems.AddRange(_branches.BranchClashes(input.To, allRepos, from));

        if (_git.DirtyPaths(input.Master).Count > 0)
        {
            problems.Add($"the change's copy {input.Master} has uncommitted files");
        }

        foreach (var r in holders)
        {
            var where = _git.CheckedOutIn(r.Path, from);
            if (where is not null && _git.DirtyPaths(where).Count > 0)
            {
                problems.Add($"{r.Repo} has {from} checked out with uncommitted files");
            }
        }

        if (problems.Count > 0)
        {
            return new RenameOutcome(problems, [], from);
        }

        var done = new List<RepositoryRef>();
        foreach (var r in new[] { new RepositoryRef("master", input.Master) }.Concat(holders))
        {
            var moved = _git.Run(r.Path, ["branch", "-m", from, input.To]);
            if (!moved.Ok)
            {
                done.Reverse();
                foreach (var back in done)
                {
                    _git.Run(back.Path, ["branch", "-m", input.To, from]);
                }

                return new RenameOutcome([$"git could not rename {from} in {r.Repo}: {moved.Stderr.Trim()}; nothing was renamed"], [], from);
            }

            done.Add(r);
        }

        var file = _changeRecords.Write(folder, record with { Branch = input.To });
        var header = $"docs({input.Scope}): rename branch {input.Change}";
        var committed = _git.Run(input.Master, ["commit", "--quiet", "-m", header, "-m", Committer.Trailer, "--", file]);
        if (!committed.Ok)
        {
            var detail = (string.IsNullOrEmpty(committed.Stderr) ? committed.Stdout : committed.Stderr).Trim();
            problems.Add($"the branch is renamed, but the commit \"{header}\" failed: {detail}");
        }

        return new RenameOutcome(problems, done.Select(r => r.Repo).ToList(), from);
    }
}
