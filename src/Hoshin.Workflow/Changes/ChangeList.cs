using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

public sealed record ListedChange(string Change, string? Title, string Track, string Master, bool InCopy, string? Finished);

public sealed record ListChangesResult(string System, List<ListedChange> Changes, List<string> Problems);

/// <summary>Cheap change listing reading only intent documents.</summary>
public sealed class ChangeListService(ChangePlacer placer, FinishedChecker finishedChecker)
{
    private readonly ChangePlacer _placer = placer ?? throw new ArgumentNullException(nameof(placer));
    private readonly FinishedChecker _finished = finishedChecker ?? throw new ArgumentNullException(nameof(finishedChecker));

    public ListChangesResult List(string cwd, ResolveOptions resolveOptions, string? system = null)
    {
        var place = _placer.PlaceChanges(cwd, new StateOptions(resolveOptions.Master, resolveOptions.Workspace, system));
        var finished = _finished.FinishedOf(place);
        var changes = new List<ListedChange>();

        foreach (var c in place.Placed)
        {
            var file = Path.Combine(c.Top, "intent", place.System, c.Change, "intent.md");
            var headPath = $"intent/{place.System}/{c.Change}/intent.md";
            var working = File.Exists(file) ? File.ReadAllText(file) : null;
            var head = place.Reads[c.Top].HeadText(headPath);
            var (title, track) = ChangePlacer.TitleAndTrack(working, head, file, $"HEAD:{headPath}");
            changes.Add(new ListedChange(c.Change, title, track, c.Top, c.InCopy, finished.GetValueOrDefault(c.Change)));
        }

        return new ListChangesResult(place.System, changes, []);
    }
}
