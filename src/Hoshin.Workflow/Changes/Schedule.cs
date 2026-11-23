using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Changes;

public sealed record WaitingOnEntry(string Task, string Repo);

public sealed record ScheduledRepo(string Repo, string State, string? Task, List<WaitingOnEntry> WaitingOn);

public sealed record ChangeSchedule(List<ScheduledRepo> Repos, string Next, bool OneAtATime);

/// <summary>Minimal task projection needed for cross-repository scheduling.</summary>
public sealed record ScheduledTaskInput(string Id, string Repo, IReadOnlyList<string> DependsOn);

/// <summary>Computes the multi-repo / single-repo build schedule from plan tasks and logged entries.</summary>
public static class ScheduleCalculator
{
    public static ChangeSchedule Compute(IReadOnlyList<ParsedTask> tasks, ISet<string> logged, bool oneAtATime) =>
        Compute(
            tasks.Select(t => new ScheduledTaskInput(t.Id, t.Repo, t.DependsOn)).ToList(),
            logged,
            oneAtATime);

    public static ChangeSchedule Compute(IReadOnlyList<ScheduledTaskInput> tasks, ISet<string> logged, bool oneAtATime)
    {
        var repoOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var repoOrder = new List<string>();
        var seenRepos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tasks)
        {
            repoOf[t.Id] = t.Repo;
            if (seenRepos.Add(t.Repo))
            {
                repoOrder.Add(t.Repo);
            }
        }

        var repos = new List<ScheduledRepo>();
        foreach (var repo in repoOrder)
        {
            var first = tasks.FirstOrDefault(t => t.Repo == repo && !logged.Contains(t.Id));
            if (first is null)
            {
                repos.Add(new ScheduledRepo(repo, "done", null, []));
                continue;
            }

            var waitingOn = new List<WaitingOnEntry>();
            var seenDeps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in first.DependsOn)
            {
                if (seenDeps.Add(id) && !logged.Contains(id) && repoOf.TryGetValue(id, out var depRepo) && depRepo != repo)
                {
                    waitingOn.Add(new WaitingOnEntry(id, depRepo));
                }
            }

            repos.Add(new ScheduledRepo(repo, waitingOn.Count > 0 ? "waiting" : "ready", first.Id, waitingOn));
        }

        var next = repos.Any(r => r.State == "ready")
            ? "build"
            : repos.All(r => r.State == "done")
                ? "review"
                : "blocked";

        return new ChangeSchedule(repos, next, oneAtATime);
    }
}
