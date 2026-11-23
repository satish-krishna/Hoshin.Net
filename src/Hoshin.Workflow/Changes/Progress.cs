using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

/// <summary>Reads a change's plan tasks and build-log progress.</summary>
public sealed class ProgressService(PlanReader planReader, BuildLogReader buildLogReader, MarkerReader markerReader)
{
    private readonly PlanReader _plan = planReader ?? throw new ArgumentNullException(nameof(planReader));
    private readonly BuildLogReader _buildLog = buildLogReader ?? throw new ArgumentNullException(nameof(buildLogReader));
    private readonly MarkerReader _markerReader = markerReader ?? throw new ArgumentNullException(nameof(markerReader));

    public List<ParsedTask>? PlanTasks(string folder)
    {
        var file = Path.Combine(folder, "plan.md");
        if (!File.Exists(file))
        {
            return null;
        }

        return TasksOf(File.ReadAllText(file), file);
    }

    public List<ParsedTask>? TasksOf(string text, string file)
    {
        var marker = _markerReader.FindMarker(file);
        var fm = FrontMatter.Parse(text, file);
        var system = Jsonc.String(fm.Data, "system") ?? "";
        var allowed = MarkerReader.AllowedPlanRepos(marker, system);
        var result = _plan.Read(text, file, allowed, system);
        return result.Problems.Count == 0 ? result.Document.Tasks : null;
    }

    public HashSet<string> LoggedTasks(string folder, string repo)
    {
        var file = Path.Combine(folder, "build", $"{repo}.md");
        return File.Exists(file) ? EntriesOf(File.ReadAllText(file), file) : new HashSet<string>(StringComparer.Ordinal);
    }

    public HashSet<string> EntriesOf(string text, string file)
    {
        var result = _buildLog.Read(text, file);
        return result.Document.Entries.Select(e => e.Task).ToHashSet(StringComparer.Ordinal);
    }

    public List<string> PlanRepos(string folder)
    {
        var tasks = PlanTasks(folder);
        if (tasks is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var repos = new List<string>();
        foreach (var t in tasks)
        {
            if (seen.Add(t.Repo))
            {
                repos.Add(t.Repo);
            }
        }

        return repos;
    }

    public List<string>? UnloggedTasks(string folder, string repo)
    {
        var tasks = PlanTasks(folder);
        if (tasks is null)
        {
            return null;
        }

        var logged = LoggedTasks(folder, repo);
        return tasks.Where(t => t.Repo == repo && !logged.Contains(t.Id)).Select(t => t.Id).ToList();
    }
}
