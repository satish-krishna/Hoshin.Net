using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Workspace;

/// <summary>Checks whether a candidate branch name collides with existing branches across repositories.</summary>
public sealed class BranchChecker(IGitBranches git)
{
    private readonly IGitBranches _git = git ?? throw new ArgumentNullException(nameof(git));

    public List<string> BranchClashes(string name, IReadOnlyList<RepositoryRef> repos, string? own = null)
    {
        var lower = name.ToLowerInvariant();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in repos)
        {
            foreach (var existing in _git.BranchNames(r.Path))
            {
                if (own is not null && existing == own)
                {
                    continue;
                }

                var other = existing.ToLowerInvariant();
                string? problem = null;
                if (other == lower)
                {
                    problem = $"{name} is taken in {r.Repo} by the branch {existing}";
                }
                else if (other.StartsWith(lower + "/", StringComparison.Ordinal) || lower.StartsWith(other + "/", StringComparison.Ordinal))
                {
                    problem = $"{name} clashes in {r.Repo} with the branch {existing}: one would be a folder of the other";
                }

                if (problem is not null && seen.Add(problem))
                {
                    problems.Add(problem);
                }
            }
        }

        return problems;
    }
}
