using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

public sealed record DocumentFacts(bool InHead, string? HeadStatus, string? WorkingStatus);

public sealed record FixFacts(
    DocumentFacts Intent,
    DocumentFacts Spec,
    DocumentFacts Plan,
    List<string> Unrecorded,
    bool OwnLinesOnly,
    string Accepted,
    bool AcceptanceLast,
    bool Built);

public sealed record FixStateResult(int Row, FixFacts Facts);

public sealed record FixStateBatchEntry(string Folder, FixStateResult? Result, string? Error);

public sealed record FixInput(
    string Top,
    string Prefix,
    string Folder,
    bool HasHead,
    Func<string, string?> HeadText,
    Func<string, List<string>> HeadPaths,
    Func<HashSet<string>> GetReported);

/// <summary>Computes where a light-track fix stands per the 8-row fix-state table in GRAMMAR.md.</summary>
public sealed class FixStateService(
    IGitClient? git = null,
    PlanReader? planReader = null,
    BuildLogReader? buildLogReader = null)
{
    public static FixStateService Default { get; } = new();

    private static readonly string[] Documents = ["intent", "spec", "plan"];
    private static readonly HashSet<string> OwnLines = ["status", "source_intent_commit", "source_spec_commit", "accepted_in_advance"];
    private static readonly Regex JsoncFieldLine = new(@"^\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*:\s*(.+?)\s*,?\s*(?://.*)?$", RegexOptions.Compiled);
    private static readonly Regex HunkRegex = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;
    private readonly PlanReader _plan = planReader ?? new PlanReader();
    private readonly BuildLogReader _buildLog = buildLogReader ?? new BuildLogReader();

    public static string? StatusOf(string text, string file) => FrontMatter.TryGetScalar(text, file, "status");

    public static int RowOf(FixFacts facts)
    {
        var docs = new[] { facts.Intent, facts.Spec, facts.Plan };
        if (facts.Intent.HeadStatus == "rejected")
        {
            return 1;
        }

        var oddStatus = docs.Any(d => d.InHead && d.HeadStatus != "draft" && d.HeadStatus != "accepted");
        var someAccepted = facts.Accepted != "none";
        if (oddStatus || facts.Accepted == "other" || (someAccepted && docs.Any(d => !d.InHead)))
        {
            return 2;
        }

        if (facts.Unrecorded.Count > 0)
        {
            return 3;
        }

        if (facts.OwnLinesOnly)
        {
            return 4;
        }

        if (facts.Accepted == "leading" && facts.AcceptanceLast)
        {
            return 5;
        }

        if (facts.Accepted == "none" && !facts.Built)
        {
            return 6;
        }

        if (facts.Accepted != "all")
        {
            return 7;
        }

        return 8;
    }

    public FixStateResult Compute(string folderArg)
    {
        var folder = Path.GetFullPath(folderArg);
        if (!File.Exists(folder) && !Directory.Exists(folder))
        {
            throw new CouldNotRunException($"{folderArg} does not exist");
        }

        if (!Directory.Exists(folder))
        {
            throw new CouldNotRunException($"{folderArg} is not a folder");
        }

        var top = _git.Must(folder, ["rev-parse", "--show-toplevel"], "git rev-parse");
        var prefix = _git.Must(folder, ["rev-parse", "--show-prefix"], "git rev-parse");
        var hasHead = _git.CommitOf(top, "HEAD") is not null;
        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        var paths = Documents.Select(d => $"{prefix}{d}.md").ToList();
        HashSet<string>? reported = null;

        return ComputeFromInput(new FixInput(
            top,
            prefix,
            folder,
            hasHead,
            path =>
            {
                if (!texts.TryGetValue(path, out var value))
                {
                    var inHead = hasHead && _git.Must(top, ["ls-tree", "--name-only", "HEAD", "--", path], "git ls-tree").Length > 0;
                    value = inHead ? _git.Run(top, ["show", $"HEAD:{path}"]).Stdout : null;
                    texts[path] = value;
                }

                return value;
            },
            dir => _git.Must(top, ["ls-tree", "-z", "--name-only", "HEAD", "--", dir], "git ls-tree")
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .ToList(),
            () => reported ??= _git.ReportedPaths(top, paths)));
    }

    public List<FixStateBatchEntry> ComputeBatch(IReadOnlyList<string> folders) =>
        folders.Select(folder =>
        {
            try
            {
                return new FixStateBatchEntry(folder, Compute(folder), null);
            }
            catch (CouldNotRunException e)
            {
                return new FixStateBatchEntry(folder, null, e.Message);
            }
            catch (Exception e)
            {
                return new FixStateBatchEntry(folder, null, $"internal error: {e.Message}");
            }
        }).ToList();

    public FixStateResult ComputeFromInput(FixInput input)
    {
        var paths = Documents.Select(d => $"{input.Prefix}{d}.md").ToArray();
        var head = paths.Select(p => input.HasHead ? input.HeadText(p) : null).ToArray();
        var working = Documents.Select(d =>
        {
            var p = Path.Combine(input.Folder, $"{d}.md");
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }).ToArray();

        var intentFile = Path.Combine(input.Folder, "intent.md");
        if (working[0] is null && head[0] is null)
        {
            throw new CouldNotRunException($"{intentFile}: cannot read the intent: there is no intent.md in the working tree or in HEAD");
        }

        var track = working[0] is null ? null : FrontMatter.TryGetScalar(working[0]!, intentFile, "track");
        track ??= head[0] is null ? null : FrontMatter.TryGetScalar(head[0]!, $"HEAD:{paths[0]}", "track");
        if (track != "fix")
        {
            var shown = track is null ? "not set" : System.Text.Json.JsonSerializer.Serialize(track);
            throw new CouldNotRunException($"{intentFile}: not a fix (its track is {shown}, expected fix)");
        }

        var perDoc = new DocumentFacts[3];
        for (var i = 0; i < 3; i++)
        {
            perDoc[i] = new DocumentFacts(
                head[i] is not null,
                head[i] is null ? null : StatusOf(head[i]!, $"HEAD:{paths[i]}"),
                working[i] is null ? null : StatusOf(working[i]!, Path.Combine(input.Folder, $"{Documents[i]}.md")));
        }

        var acceptedInHead = perDoc.Select(d => d.InHead && d.HeadStatus == "accepted").ToArray();
        var count = acceptedInHead.Count(a => a);
        var leading = count > 0 && acceptedInHead.Take(count).All(a => a);
        var accepted = count == 0 ? "none" : count == 3 ? "all" : leading ? "leading" : "other";

        var subject = input.HasHead ? _git.Must(input.Top, ["log", "-1", "--format=%s", "--", .. paths], "git log") : "";
        var change = Path.GetFileName(input.Folder);
        var acceptanceLast = Documents.Any(d => subject.EndsWith($"accept {d} {change}", StringComparison.Ordinal));

        var initial = new FixFacts(perDoc[0], perDoc[1], perDoc[2], [], false, accepted, acceptanceLast, false);
        var reported = input.GetReported();
        var early = RowOf(initial);
        if (early is 1 or 2)
        {
            var earlyUnrecorded = Enumerable.Range(0, 3)
                .Where(i => !perDoc[i].InHead || reported.Contains(paths[i]))
                .Select(i => Documents[i])
                .ToList();
            return new FixStateResult(early, initial with { Unrecorded = earlyUnrecorded });
        }

        var carrier = Array.FindIndex(acceptedInHead, a => !a);
        var unrecorded = new List<string>();
        var anyDiff = false;
        for (var i = 0; i < 3; i++)
        {
            if (!perDoc[i].InHead)
            {
                unrecorded.Add(Documents[i]);
                continue;
            }

            if (!reported.Contains(paths[i]))
            {
                continue;
            }

            if (working[i] is not null && !_git.DiffersFromHead(input.Top, paths[i]))
            {
                continue;
            }

            anyDiff = true;
            var ownLines = i == carrier &&
                working[i] is not null &&
                perDoc[i].WorkingStatus == "accepted" &&
                OnlyOwnLines(input.Top, paths[i], head[i]!, working[i]!);
            if (!ownLines)
            {
                unrecorded.Add(Documents[i]);
            }
        }

        var built = input.HasHead && IsBuilt(input);
        var facts = initial with { Unrecorded = unrecorded, OwnLinesOnly = anyDiff && unrecorded.Count == 0, Built = built };
        return new FixStateResult(RowOf(facts), facts);
    }

    private bool OnlyOwnLines(string top, string path, string headText, string workingText)
    {
        var args = new List<string> { "diff", "HEAD" };
        args.AddRange(ProcessGitClient.DiffOptions);
        args.AddRange(["--no-color", "--unified=0", "--", path]);
        var diff = _git.Run(top, args);
        if (!diff.Ok)
        {
            throw new CouldNotRunException($"git diff failed in {top}: {(string.IsNullOrEmpty(diff.Stderr) ? diff.Stdout : diff.Stderr).Trim()}");
        }

        var headLines = headText.Split('\n');
        var endOld = FrontMatter.CloseLine(headText);
        var endNew = FrontMatter.CloseLine(workingText);
        var nextOld = 0;
        var nextNew = 0;
        var oldStart = 0;
        var oldCount = 0;
        var added = 0;
        var inHunks = false;
        var statusAccepted = false;
        var removedOther = new List<string>();
        var addedOther = new List<string>();

        bool FlushCommaOnly()
        {
            if (removedOther.Count != addedOther.Count)
            {
                return false;
            }

            for (var k = 0; k < removedOther.Count; k++)
            {
                if (removedOther[k].TrimEnd(',') != addedOther[k].TrimEnd(','))
                {
                    return false;
                }
            }

            removedOther.Clear();
            addedOther.Clear();
            return true;
        }

        foreach (var line in diff.Stdout.Split('\n'))
        {
            var hunk = HunkRegex.Match(line);
            if (hunk.Success)
            {
                if (!FlushCommaOnly())
                {
                    return false;
                }

                inHunks = true;
                oldStart = int.Parse(hunk.Groups[1].Value);
                oldCount = hunk.Groups[2].Success ? int.Parse(hunk.Groups[2].Value) : 1;
                nextOld = oldStart;
                nextNew = int.Parse(hunk.Groups[3].Value);
                added = 0;
                continue;
            }

            if (!inHunks || line.Length == 0)
            {
                continue;
            }

            var side = line[0];
            if (side != '-' && side != '+')
            {
                return false;
            }

            var number = side == '-' ? nextOld++ : nextNew++;
            var endLine = side == '-' ? endOld : endNew;
            var text = line[1..];
            if (side == '+')
            {
                var index = added < oldCount ? oldStart - 1 + added : oldCount > 0 ? oldStart + oldCount - 2 : oldStart - 1;
                added++;
                var expectsCr = index >= 0 && index < headLines.Length && headLines[index].EndsWith('\r');
                if (expectsCr != text.EndsWith('\r'))
                {
                    return false;
                }
            }

            if (text.EndsWith('\r'))
            {
                text = text[..^1];
            }

            if (number < 2 || number >= endLine)
            {
                return false;
            }

            var field = JsoncFieldLine.Match(text);
            if (!field.Success || !OwnLines.Contains(field.Groups[1].Value))
            {
                (side == '-' ? removedOther : addedOther).Add(text);
                continue;
            }

            if (side == '+' && field.Groups[1].Value == "status")
            {
                var rawVal = field.Groups[2].Value.Trim();
                if (rawVal != "\"accepted\"")
                {
                    return false;
                }

                statusAccepted = true;
            }
        }

        return FlushCommaOnly() && statusAccepted;
    }

    private bool IsBuilt(FixInput input)
    {
        var planText = input.HeadText($"{input.Prefix}plan.md");
        if (planText is null)
        {
            return false;
        }

        var planFile = Path.Combine(input.Folder, "plan.md");
        PlanReadResult? planResult;
        try
        {
            var marker = MarkerReader.FindMarker(planFile);
            var fm = FrontMatter.Parse(planText, planFile);
            var system = Jsonc.String(fm.Data, "system") ?? "";
            planResult = _plan.Read(planText, planFile, MarkerReader.AllowedPlanRepos(marker, system), system);
        }
        catch (CouldNotRunException)
        {
            return false;
        }

        var tasks = planResult.Document.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        if (tasks.Count == 0)
        {
            return false;
        }

        var dir = $"{input.Prefix}build/";
        var logs = input.HeadPaths(dir)
            .Where(p => p.StartsWith(dir, StringComparison.Ordinal) &&
                !p[dir.Length..].Contains('/') &&
                p.EndsWith(".md", StringComparison.Ordinal) &&
                !p.EndsWith("/review.md", StringComparison.Ordinal));

        foreach (var path in logs)
        {
            var text = input.HeadText(path);
            if (text is null)
            {
                continue;
            }

            try
            {
                var logResult = _buildLog.Read(text, Path.Combine(input.Folder, "build", Path.GetFileName(path)));
                if (logResult.Document.Entries.Any(e => tasks.Contains(e.Task)))
                {
                    return true;
                }
            }
            catch (CouldNotRunException)
            {
                // Unreadable build log counts as not built.
            }
        }

        return false;
    }
}
