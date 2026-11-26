using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Computes the canonical code head across single-repo and multi-repo changes so a fix commit in any touched
/// product repository invalidates stale checks and reviews while master-only <c>build/review.md</c> commits do not.
/// </summary>
public static class WorkflowCodeHead
{
    public static string Compute(WorkflowRepo repo, IReadOnlyDictionary<string, string>? repoPaths)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (repoPaths is null || repoPaths.Count == 0)
        {
            return repo.CodeHead();
        }

        var hasExternalRepo = repoPaths.Values.Any(p => !PathHelpers.PathsEqual(p, repo.Root));
        if (!hasExternalRepo)
        {
            return repo.CodeHead();
        }

        var entries = new List<string>(repoPaths.Count);
        foreach (var (name, path) in repoPaths.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var sha = PathHelpers.PathsEqual(path, repo.Root)
                ? repo.CodeHead()
                : repo.GitQuery.CommitOf(path, "HEAD") ?? "";
            if (!string.IsNullOrEmpty(sha))
            {
                entries.Add($"{name}={sha}");
            }
        }

        return entries.Count > 0 ? string.Join(',', entries) : repo.CodeHead();
    }

    public static string Short(string sha)
    {
        if (string.IsNullOrEmpty(sha))
        {
            return sha;
        }

        if (!sha.Contains('=', StringComparison.Ordinal))
        {
            return sha.Length > 7 ? sha[..7] : sha;
        }

        var parts = sha.Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && parts[i].Length - eq - 1 > 7)
            {
                parts[i] = parts[i][..(eq + 1 + 7)];
            }
        }

        return string.Join(',', parts);
    }
}
