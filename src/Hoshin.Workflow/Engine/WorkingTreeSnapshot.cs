using System.Security.Cryptography;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>One working-tree or index change parsed from <c>git status --porcelain=v1 -z -uall</c>.</summary>
public sealed record WorkingTreeChange(string Path, bool Deleted, bool Staged);

/// <summary>Pre-issue working-tree snapshot captured when a task attempt is issued.</summary>
public sealed record WorkingTreeBaseline(bool IndexHadStagedChanges, Dictionary<string, string> PathDigests);

/// <summary>
/// Captures, parses, and diffs working-tree changes using NUL-delimited porcelain output so pre-existing
/// untracked/dirty files and unrelated staged entries never leak into task commits.
/// </summary>
public static class WorkingTreeSnapshot
{
    public const string StagedIndexRefusalReason = "git: the index had staged changes before the attempt";

    private const string DeletedDigest = "<deleted>";
    private const string MissingDigest = "<missing>";

    public static IReadOnlyList<WorkingTreeChange> GetChanges(WorkflowRepo repo, IGitStatus? gitStatus = null)
    {
        var output = (gitStatus ?? repo.GitStatus).PorcelainStatusZ(repo.Root);
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        return ParsePorcelainZ(output);
    }

    public static WorkingTreeBaseline Capture(WorkflowRepo repo, IGitStatus? gitStatus = null)
    {
        var changes = GetChanges(repo, gitStatus);
        var hasStaged = changes.Any(c => c.Staged);
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            digests[change.Path] = ComputePathDigest(repo.Root, change.Path, change.Deleted);
        }

        return new WorkingTreeBaseline(hasStaged, digests);
    }

    public static List<WorkingTreeChange> FilterTaskChanges(
        WorkflowRepo repo,
        IReadOnlyList<WorkingTreeChange> current,
        IReadOnlyDictionary<string, string>? preIssuePaths)
    {
        if (preIssuePaths is null || preIssuePaths.Count == 0)
        {
            return [.. current];
        }

        var filtered = new List<WorkingTreeChange>(current.Count);
        foreach (var change in current)
        {
            if (preIssuePaths.TryGetValue(change.Path, out var beforeDigest))
            {
                var afterDigest = ComputePathDigest(repo.Root, change.Path, change.Deleted);
                if (string.Equals(beforeDigest, afterDigest, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            filtered.Add(change);
        }

        return filtered;
    }

    public static IReadOnlyList<WorkingTreeChange> ParsePorcelainZ(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var list = new List<WorkingTreeChange>();
        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 4 || token[2] != ' ')
            {
                continue;
            }

            var x = token[0];
            var y = token[1];
            var path = NormalizePath(token[3..]);
            var staged = x != ' ' && x != '?';

            if (x is 'R' or 'C' || y is 'R' or 'C')
            {
                if (i + 1 < tokens.Length && tokens[i + 1].Length > 0)
                {
                    var oldPath = NormalizePath(tokens[++i]);
                    if (x == 'R' || y == 'R')
                    {
                        list.Add(new WorkingTreeChange(oldPath, Deleted: true, Staged: staged));
                    }
                }

                list.Add(new WorkingTreeChange(path, Deleted: false, Staged: staged));
                continue;
            }

            var deleted = x == 'D' || y == 'D';
            list.Add(new WorkingTreeChange(path, deleted, staged));
        }

        return list;
    }

    private static string ComputePathDigest(string repoRoot, string relativePath, bool deleted)
    {
        if (deleted)
        {
            return DeletedDigest;
        }

        var fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return MissingDigest;
        }

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
    }

    private static string NormalizePath(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
}
