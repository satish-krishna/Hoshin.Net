using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Changes;

/// <summary>Determines whether each placed change is open (null), <c>rejected</c>, or <c>merged</c>.</summary>
public sealed class FinishedChecker(IGitClient git)
{
    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));

    public Dictionary<string, string?> FinishedOf(Placement p)
    {
        var output = new Dictionary<string, string?>(StringComparer.Ordinal);
        var masterRead = p.Reads[p.Master];

        foreach (var (top, read) in p.Reads)
        {
            var paths = p.Placed
                .Where(c => PathHelpers.PathsEqual(c.Top, top))
                .Select(c => $"intent/{p.System}/{c.Change}/intent.md");
            read.LoadTexts(paths);
        }

        var baseBranch = _git.BaseBranch(p.Master);
        List<string> OnBase(string change) => baseBranch is null
            ? []
            : [$"refs/heads/{baseBranch}:intent/{p.System}/{change}/intent.md", $"refs/remotes/origin/{baseBranch}:intent/{p.System}/{change}/intent.md"];

        var open = new List<string>();
        foreach (var placed in p.Placed)
        {
            var path = $"intent/{p.System}/{placed.Change}/intent.md";
            var text = p.Reads[placed.Top].HeadText(path);
            if (text is not null && FixStateService.StatusOf(text, $"HEAD:{path}") == "rejected")
            {
                output[placed.Change] = "rejected";
            }
            else
            {
                output[placed.Change] = null;
                var copyPath = PathHelpers.RealPath(Path.Combine(ChangeLocator.CopiesFolder(p.Workspace), placed.Change));
                if (!p.Copies.Any(c => PathHelpers.PathsEqual(c, copyPath)))
                {
                    open.Add(placed.Change);
                }
            }
        }

        masterRead.Resolve(open.SelectMany(OnBase));
        foreach (var change in open)
        {
            if (OnBase(change).Any(name => masterRead.ObjectOf(name) is not null))
            {
                output[change] = "merged";
            }
        }

        return output;
    }
}
