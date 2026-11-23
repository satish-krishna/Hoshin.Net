using System.Text.RegularExpressions;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Migration;

namespace Hoshin.Workflow.Workspace;

public sealed record MigrateStep(string Step, string Path, string? To, string Status, string? Detail);

public sealed record MigrateResult(
    string Workspace,
    string Master,
    WorkspaceLayout Layout,
    bool Write,
    List<MigrateStep> Steps,
    List<string> Kept,
    List<string> Manual,
    List<string> Problems);

/// <summary>Migrates a workspace from legacy <c>ai-sdlc</c> names to <c>hoshin</c> names and rewrites legacy YAML documents / <c>change.yml</c> to Layout 2 JSONC (idempotent, dry-run by default).</summary>
public sealed class WorkspaceMigrator(IGitClient git, IWorkspaceResolver resolver, IDocumentMigrator documentMigrator)
{
    private const string OldState = "ai-sdlc";
    private const string NewState = "hoshin";
    private const string OldStart = "<!-- ai-sdlc:start -->";
    private const string OldEnd = "<!-- ai-sdlc:end -->";
    private const string RerunSetup = "run the hoshin setup skill with `--instructions` to write the new block, then delete the old block between the `ai-sdlc` markers";
    private static readonly string[] MergeOrRebaseFiles = ["MERGE_HEAD", "rebase-merge", "rebase-apply"];
    private static readonly Regex LineSplitWithNewline = new(@"(?<=\n)", RegexOptions.Compiled);

    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IDocumentMigrator _documents = documentMigrator ?? throw new ArgumentNullException(nameof(documentMigrator));

    private sealed record PlannedStep(MigrateStep Step, Func<string?>? Act, string? Branch = null);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public MigrateResult Migrate(string cwd, ResolveOptions options, bool write)
    {
        var resolved = _resolver.Resolve(cwd, options, MarkerConstants.AnyMarkers);
        if (resolved.Master is null || resolved.Workspace is null)
        {
            throw new CouldNotRunException(resolved.Problems.Count > 0 ? string.Join('\n', resolved.Problems) : "no master repository");
        }

        var master = resolved.Master;
        var workspace = resolved.Workspace;
        var layout = resolved.Layout;
        var wts = _git.Worktrees(master);
        if (wts.Count == 0)
        {
            throw new CouldNotRunException($"{master} has no checkout");
        }

        var main = PathHelpers.RealPath(wts[0].Path);
        var markerFile = MarkerReader.MarkerFileIn(main, MarkerConstants.AnyMarkers)
            ?? MarkerReader.MarkerFileIn(master, MarkerConstants.AnyMarkers)!;
        var marker = MarkerReader.ReadMarker(markerFile);

        var (groups, moving) = Plan(workspace, master, main, marker);
        var planned = groups.SelectMany(g => g).ToList();
        var problems = planned
            .Where(p => p.Step.Status == "refused")
            .Select(p => $"{p.Step.Step} {p.Step.Path}: {p.Step.Detail}")
            .ToList();

        var steps = new List<MigrateStep>();
        var kept = KeptEntries(workspace, moving);

        if (problems.Count > 0 || !write)
        {
            steps.AddRange(planned.Select(p => p.Step));
        }
        else
        {
            var failed = false;
            for (var index = 0; index < groups.Count; index++)
            {
                foreach (var p in groups[index])
                {
                    if (p.Act is null)
                    {
                        steps.Add(p.Step);
                        continue;
                    }

                    var error = p.Act();
                    if (error is null)
                    {
                        steps.Add(p.Step with { Status = "done" });
                        continue;
                    }

                    steps.Add(p.Step with { Status = "refused", Detail = error });
                    problems.Add($"{p.Step.Step} {p.Step.Path}: {error}");
                    failed = true;
                    break;
                }

                if (failed)
                {
                    break;
                }

                if (index == 1)
                {
                    RemoveEmptyOldFolders(workspace);
                }
            }

            kept = KeptEntries(workspace, []);
        }

        var pushed = planned.Where(p => p.Act is not null && !string.IsNullOrEmpty(p.Branch)).Select(p => p.Branch!).Distinct(StringComparer.Ordinal).ToList();
        var manual = new List<string>
        {
            pushed.Count > 0
                ? $"push the marker commit on {string.Join(", ", pushed)}"
                : "push any marker commit not yet pushed; this run made none",
            "install the hoshin plugin (`hoshin@hoshin`) and uninstall the ai-sdlc plugin (`ai-sdlc@ai-sdlc`)",
        };

        foreach (var s in steps.Where(s => s.Step == "instructions" && s.Status == "manual"))
        {
            manual.Add($"instructions: {s.Detail}");
        }

        if (layout == WorkspaceLayout.SingleRepo && steps.Any(s => s.Step == "instructions" && s.Status is "done" or "would"))
        {
            manual.Add("commit CLAUDE.md, which now holds the new block: `commit --kind instructions -- CLAUDE.md`");
        }

        return new MigrateResult(workspace, master, layout, write, steps, kept, manual, problems);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private (List<List<PlannedStep>> Groups, List<string> Moving) Plan(string workspace, string master, string main, Marker marker)
    {
        var oldCopies = Path.Combine(workspace, ".ai-sdlc", "copies");
        var newCopies = Path.Combine(workspace, ".hoshin", "copies");
        var oldCopiesReal = PathHelpers.RealPath(oldCopies);
        var newCopiesReal = PathHelpers.RealPath(newCopies);

        var checkouts = _git.Worktrees(main)
            .Select(w => w with { Path = PathHelpers.RealPath(w.Path) })
            .Where(w => !w.Prunable && Directory.Exists(w.Path))
            .ToList();

        bool IsCopy(GitWorktree w)
        {
            var parent = Path.GetDirectoryName(w.Path);
            return parent is not null && (PathHelpers.PathsEqual(parent, oldCopiesReal) || PathHelpers.PathsEqual(parent, newCopiesReal));
        }

        var markers = checkouts.Select(w => MarkerStep(w.Path, w.Branch, IsCopy(w) ? Path.GetFileName(w.Path) : null)).ToList();
        var moving = checkouts.Where(w => Path.GetDirectoryName(w.Path) is { } p && PathHelpers.PathsEqual(p, oldCopiesReal)).ToList();
        var copies = moving.Select(w =>
        {
            var to = Path.Combine(newCopies, Path.GetFileName(w.Path));
            if (Directory.Exists(to) || File.Exists(to))
            {
                return new PlannedStep(new MigrateStep("copy", w.Path, to, "refused", $"{to} already exists"), null);
            }

            return new PlannedStep(new MigrateStep("copy", w.Path, to, "would", null), () =>
            {
                Directory.CreateDirectory(newCopies);
                var moved = _git.Run(main, ["worktree", "move", w.Path, to]);
                return moved.Ok ? null : (string.IsNullOrEmpty(moved.Stderr) ? moved.Stdout : moved.Stderr).Trim();
            });
        }).ToList();

        var repositories = new List<string> { main };
        if (marker.Layout == WorkspaceLayout.Workspace)
        {
            foreach (var sys in marker.Systems.Values)
            {
                foreach (var name in sys.Repos)
                {
                    var path = Path.Combine(workspace, name);
                    if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
                    {
                        repositories.Add(path);
                    }
                }
            }
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var stateSteps = repositories
            .SelectMany(StateFolders)
            .Where(seen.Add)
            .Select(StateStep)
            .ToList();

        var instructionStep = InstructionsStep(workspace, main, marker, master);
        var documentSteps = DocumentSteps(checkouts, oldCopiesReal, newCopies);
        List<List<PlannedStep>> allGroups = [markers, copies, stateSteps, [instructionStep]];
        if (documentSteps.Count > 0)
        {
            allGroups.Add(documentSteps);
        }

        return (allGroups, moving.Select(w => Path.GetFileName(w.Path)).ToList());
    }

    private List<PlannedStep> DocumentSteps(List<GitWorktree> checkouts, string oldCopiesReal, string newCopies)
    {
        var planned = new List<PlannedStep>();
        foreach (var wt in checkouts)
        {
            var intentDir = Path.Combine(wt.Path, "intent");
            if (!Directory.Exists(intentDir))
            {
                continue;
            }

            var isMoving = Path.GetDirectoryName(wt.Path) is { } parent && PathHelpers.PathsEqual(parent, oldCopiesReal);
            var targetRoot = isMoving ? Path.Combine(newCopies, Path.GetFileName(wt.Path)) : wt.Path;

            foreach (var item in _documents.PlanDirectory(intentDir))
            {
                if (item.Status == "refused")
                {
                    planned.Add(new PlannedStep(new MigrateStep("document", item.Path, item.To, "refused", item.Detail), null));
                    continue;
                }

                var rel = Path.GetRelativePath(wt.Path, item.Path);
                var targetFile = Path.Combine(targetRoot, rel);
                planned.Add(new PlannedStep(new MigrateStep("document", item.Path, item.To, "would", null), () =>
                {
                    var res = _documents.MigratePaths([targetFile], write: true);
                    return res.Problems.Count == 0 ? null : string.Join("; ", res.Problems);
                }));
            }
        }

        return planned;
    }

    private bool Tracked(string dir, string file) => _git.Run(dir, ["ls-files", "--error-unmatch", "--", file]).Ok;

    private List<string> MarkerRefusals(string dir)
    {
        if (File.Exists(Path.Combine(dir, MarkerConstants.LegacyFileName)) && File.Exists(Path.Combine(dir, MarkerConstants.FileName)))
        {
            return [$"both {MarkerConstants.LegacyFileName} and {MarkerConstants.FileName} exist"];
        }

        if (Tracked(dir, MarkerConstants.FileName))
        {
            return [$"both {MarkerConstants.LegacyFileName} and {MarkerConstants.FileName} are tracked"];
        }

        var why = new List<string>();
        if (!string.IsNullOrEmpty(_git.Out(dir, ["status", "--porcelain", "--", MarkerConstants.LegacyFileName])))
        {
            why.Add($"{MarkerConstants.LegacyFileName} has uncommitted changes");
        }

        if (!_git.Run(dir, ["diff", "--cached", "--quiet"]).Ok)
        {
            why.Add("the index has staged changes");
        }

        var midway = MergeOrRebaseFiles.Any(name =>
        {
            var gp = _git.Out(dir, ["rev-parse", "--git-path", name]) ?? name;
            var full = Path.IsPathRooted(gp) ? gp : Path.Combine(dir, gp);
            return File.Exists(full) || Directory.Exists(full);
        });
        if (midway)
        {
            why.Add("a merge or rebase is in progress");
        }

        if (_git.CurrentBranch(dir) is null)
        {
            why.Add("HEAD is detached");
        }

        return why;
    }

    private static string MarkerScope(string dir, string? copyName)
    {
        var systems = MarkerReader.ReadMarker(MarkerReader.MarkerFileIn(dir, MarkerConstants.AnyMarkers)!).Systems.Keys.ToList();
        if (copyName is not null)
        {
            foreach (var system in systems)
            {
                var folder = Path.Combine(dir, "intent", system, copyName);
                if (Directory.Exists(folder))
                {
                    return ScopeRules.ChangeScope(dir, system, folder, MarkerConstants.AnyMarkers);
                }
            }
        }

        return ScopeRules.SystemScope(dir, systems[0], MarkerConstants.AnyMarkers);
    }

    private PlannedStep MarkerStep(string dir, string? branch, string? copyName)
    {
        var old = Path.Combine(dir, MarkerConstants.LegacyFileName);
        var fresh = Path.Combine(dir, MarkerConstants.FileName);
        if (!Tracked(dir, MarkerConstants.LegacyFileName))
        {
            var detail = Tracked(dir, MarkerConstants.FileName) ? null : $"{MarkerConstants.LegacyFileName} is not tracked here, so there is nothing to rename";
            return new PlannedStep(new MigrateStep("marker", fresh, null, "current", detail), null);
        }

        var why = MarkerRefusals(dir);
        if (why.Count > 0)
        {
            return new PlannedStep(new MigrateStep("marker", old, fresh, "refused", string.Join("; ", why)), null);
        }

        var header = $"chore({MarkerScope(dir, copyName)}): rename the marker to {MarkerConstants.FileName}";
        return new PlannedStep(new MigrateStep("marker", old, fresh, "would", null), () =>
        {
            File.Move(old, fresh);
            var committed = _git.CommitPaths(dir, [MarkerConstants.LegacyFileName, MarkerConstants.FileName], [header, Committer.Trailer]);
            if (committed.Ok)
            {
                return null;
            }

            _git.Run(dir, ["reset", "--quiet", "--", MarkerConstants.LegacyFileName, MarkerConstants.FileName]);
            File.Move(fresh, old);
            return committed.Error;
        }, branch);
    }

    private List<string> StateFolders(string repository)
    {
        var common = _git.GitDirs(repository)!.Common;
        var linked = Path.Combine(common, "worktrees");
        var result = new List<string> { common };
        if (Directory.Exists(linked))
        {
            foreach (var dir in Directory.GetDirectories(linked))
            {
                if (Directory.Exists(Path.Combine(dir, OldState)) || Directory.Exists(Path.Combine(dir, NewState)))
                {
                    result.Add(dir);
                }
            }
        }

        return result;
    }

    private static PlannedStep StateStep(string gitDir)
    {
        var old = Path.Combine(gitDir, OldState);
        var fresh = Path.Combine(gitDir, NewState);
        if (!Directory.Exists(old))
        {
            return new PlannedStep(new MigrateStep("state", fresh, null, "current", null), null);
        }

        if (Directory.Exists(fresh))
        {
            return new PlannedStep(new MigrateStep("state", old, fresh, "refused", $"both {OldState} and {NewState} exist in {gitDir}"), null);
        }

        return new PlannedStep(new MigrateStep("state", old, fresh, "would", null), () =>
        {
            try
            {
                Directory.Move(old, fresh);
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        });
    }

    private static string OldBlockKind(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        var starts = Enumerable.Range(0, lines.Count).Where(i => lines[i] == OldStart).ToList();
        var ends = Enumerable.Range(0, lines.Count).Where(i => lines[i] == OldEnd).ToList();
        if (starts.Count == 0 && ends.Count == 0)
        {
            return "none";
        }

        if (InstructionsService.FindMarkerLines(text).Count > 0)
        {
            return "both";
        }

        return starts.Count == 1 && ends.Count == 1 && starts[0] < ends[0] ? "block" : "bad";
    }

    private PlannedStep InstructionsStep(string workspace, string main, Marker marker, string master)
    {
        var single = marker.Layout == WorkspaceLayout.SingleRepo;
        var file = Path.Combine(single ? main : workspace, "CLAUDE.md");
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var kind = OldBlockKind(text);
        if (kind == "none")
        {
            return new PlannedStep(new MigrateStep("instructions", file, null, "current", null), null);
        }

        if (kind == "both")
        {
            return new PlannedStep(new MigrateStep("instructions", file, null, "refused", $"{file} holds a block between the ai-sdlc markers and one between the hoshin markers"), null);
        }

        if (kind == "bad")
        {
            return new PlannedStep(new MigrateStep("instructions", file, null, "refused", $"{file} has an ai-sdlc start marker without its end marker, or the markers are not each there once, start first"), null);
        }

        var masterName = _resolver.MasterFolderName(workspace, master, MarkerConstants.AnyMarkers);
        var check = InstructionsService.Run(new InstructionsInput(workspace, main, masterName, marker, Write: false, AllowHome: false));
        if (check.WorkspaceMd != "current" || check.ClaudeMd == "home-folder")
        {
            var why = check.ClaudeMd == "home-folder"
                ? "the workspace is the home folder"
                : $"{Path.Combine(main, "intent", "workspace.md")} is {check.WorkspaceMd}";
            return new PlannedStep(new MigrateStep("instructions", file, null, "manual", $"{why}, so the block was left as it is; {RerunSetup}"), null);
        }

        return new PlannedStep(new MigrateStep("instructions", file, null, "would", null), () =>
        {
            var replaced = string.Concat(LineSplitWithNewline.Split(text).Select(l =>
            {
                var t = l.Trim();
                if (t == OldStart)
                {
                    return l.Replace(OldStart, InstructionsService.StartMarker, StringComparison.Ordinal);
                }

                if (t == OldEnd)
                {
                    return l.Replace(OldEnd, InstructionsService.EndMarker, StringComparison.Ordinal);
                }

                return l;
            }));
            File.WriteAllText(file, replaced);
            var res = InstructionsService.Run(new InstructionsInput(workspace, main, masterName, marker, Write: true, AllowHome: false));
            if (res.Problems.Count == 0)
            {
                return null;
            }

            File.WriteAllText(file, text);
            return string.Join(' ', res.Problems);
        });
    }

    private static List<string> KeptEntries(string workspace, IReadOnlyList<string> moving)
    {
        var root = Path.Combine(workspace, ".ai-sdlc");
        var copies = Path.Combine(root, "copies");
        static IEnumerable<string> Entries(string folder, IReadOnlyList<string> exclude) =>
            Directory.Exists(folder)
                ? Directory.GetFileSystemEntries(folder)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null && !exclude.Contains(n!))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .Select(n => Path.Combine(folder, n!))
                : [];

        return [.. Entries(copies, moving), .. Entries(root, ["copies"])];
    }

    private static void RemoveEmptyOldFolders(string workspace)
    {
        foreach (var folder in new[] { Path.Combine(workspace, ".ai-sdlc", "copies"), Path.Combine(workspace, ".ai-sdlc") })
        {
            if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
            {
                Directory.Delete(folder);
            }
        }
    }
}
