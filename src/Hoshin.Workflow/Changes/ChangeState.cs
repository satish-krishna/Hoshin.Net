using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

public sealed record DocumentState(bool Exists, bool InHead, string? HeadStatus, string? WorkingStatus, bool Uncommitted);

public sealed record PinnedDocumentState(bool Exists, bool InHead, string? HeadStatus, string? WorkingStatus, bool Uncommitted, string? Pin);

public sealed record DocumentsState(DocumentState Intent, PinnedDocumentState Spec, PinnedDocumentState Plan);

public sealed record TaskWorkState(
    List<string> All,
    List<string> Logged,
    List<string> Unlogged,
    Dictionary<string, List<string>>? Commits,
    List<string>? Untasked,
    List<string>? Uncommitted);

public sealed record ReviewSummary(int Committed, string Draft);

public sealed record AppState(string Folder, bool Touched, string? Head);

public sealed record SkillReadiness(bool Candidate, string? Stop, List<string> Accept);

public sealed record ReadinessMap(
    SkillReadiness Spec,
    SkillReadiness Plan,
    SkillReadiness Build,
    SkillReadiness Review,
    SkillReadiness Done,
    SkillReadiness Reject);

public sealed record ChangeStateItem(
    string Change,
    string Master,
    string Folder,
    bool InCopy,
    string? Title,
    string Track,
    DocumentsState Documents,
    int? Scenarios,
    Dictionary<string, TaskWorkState>? Tasks,
    ChangeSchedule? Schedule,
    Dictionary<string, string> BuildLogs,
    ReviewSummary Review,
    FixStateResult? Fix,
    Dictionary<string, AppState>? Apps,
    ReadinessMap Ready,
    string? Finished);

public sealed record StateResult(string System, List<ChangeStateItem> Changes, List<string> Problems);

/// <summary>Computes full change state and skill readiness gates per GRAMMAR.md.</summary>
public sealed class ChangeStateService(
    IGitClient? git = null,
    ChangePlacer? placer = null,
    FinishedChecker? finishedChecker = null,
    FixStateService? fixState = null,
    ProgressService? progress = null,
    SpecReader? specReader = null,
    ReviewReader? reviewReader = null,
    AppService? apps = null)
{
    public static ChangeStateService Default { get; } = new();

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;
    private readonly ChangePlacer _placer = placer ?? new ChangePlacer(git);
    private readonly FinishedChecker _finished = finishedChecker ?? new FinishedChecker(git);
    private readonly FixStateService _fixState = fixState ?? new FixStateService(git);
    private readonly ProgressService _progress = progress ?? ProgressService.Default;
    private readonly SpecReader _spec = specReader ?? new SpecReader();
    private readonly ReviewReader _review = reviewReader ?? new ReviewReader();
    private readonly AppService _apps = apps ?? new AppService(git);

    private sealed record RepositoryFacts(List<string> Uncommitted, string? From, List<TrailerCommit> Trailers);

    public StateResult Compute(string cwd, StateOptions options)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var dirsCache = new Dictionary<string, GitDirsInfo?>(cmp);
        var workCache = new Dictionary<string, RepositoryFacts>(cmp);

        var place = _placer.PlaceChanges(cwd, options, dirsCache);
        var problems = new List<string>();
        var finished = _finished.FinishedOf(place);
        var reported = options.Open
            ? place.Placed.Where(c => finished.GetValueOrDefault(c.Change) is null).ToList()
            : place.Placed;

        var pins = new Dictionary<string, List<string>>(cmp);
        var loads = new Dictionary<string, List<string>>(cmp);
        foreach (var c in reported)
        {
            var prefix = $"intent/{place.System}/{c.Change}/";
            if (!loads.TryGetValue(c.Top, out var loadList))
            {
                loadList = [];
                loads[c.Top] = loadList;
            }

            loadList.AddRange(place.Reads[c.Top].HeadPaths(prefix).Where(p => p.EndsWith(".md", StringComparison.Ordinal)));

            foreach (var (doc, field, upstream) in new[] { ("spec.md", "source_intent_commit", "intent.md"), ("plan.md", "source_spec_commit", "spec.md") })
            {
                var file = Path.Combine(c.Top, "intent", place.System, c.Change, doc);
                var sha = PinValue(ReadText(file), file, field);
                if (sha is null)
                {
                    continue;
                }

                if (!pins.TryGetValue(c.Top, out var pinList))
                {
                    pinList = [];
                    pins[c.Top] = pinList;
                }

                pinList.Add($"{sha}^{{commit}}");
                pinList.Add($"{sha}:{prefix}{upstream}");
            }
        }

        foreach (var (top, paths) in loads)
        {
            place.Reads[top].LoadTexts(paths);
        }

        foreach (var (top, list) in pins)
        {
            if (place.Reads[top].HasHead)
            {
                place.Reads[top].Resolve(list);
            }
        }

        var changes = reported
            .Select(c => ComputeOne(place.Reads[c.Top], dirsCache, workCache, place.Workspace, c.Change, c.InCopy, finished.GetValueOrDefault(c.Change), problems))
            .ToList();

        return new StateResult(place.System, changes, problems);
    }

    private ChangeStateItem ComputeOne(
        SystemRead read,
        Dictionary<string, GitDirsInfo?> dirsCache,
        Dictionary<string, RepositoryFacts> workCache,
        string workspace,
        string change,
        bool inCopy,
        string? finished,
        List<string> problems)
    {
        var top = read.Top;
        var system = read.System;
        var prefix = $"intent/{system}/{change}/";
        var folder = Path.Combine(top, "intent", system, change);
        var inHead = read.HeadPaths(prefix).ToHashSet(StringComparer.Ordinal);
        var reported = read.Reported.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

        string? HeadText(string name) => read.HeadText(prefix + name);
        string? WorkingText(string name) => ReadText(Path.Combine(folder, name));
        bool Uncommitted(string name) => reported.Contains(prefix + name) && (!inHead.Contains(prefix + name) || _git.DiffersFromHead(top, prefix + name));

        var intentHead = HeadText("intent.md");
        var intentWorking = WorkingText("intent.md");
        var specHead = HeadText("spec.md");
        var specWorking = WorkingText("spec.md");
        var planHead = HeadText("plan.md");
        var planWorking = WorkingText("plan.md");

        DocumentState DocFacts(string name, string? head, string? working) => new(
            working is not null,
            head is not null,
            head is null ? null : FixStateService.StatusOf(head, $"HEAD:{prefix}{name}"),
            working is null ? null : FixStateService.StatusOf(working, Path.Combine(folder, name)),
            Uncommitted(name));

        string? CheckPin(string docFile, string? working, string field, string upstreamFile)
        {
            if (working is null)
            {
                return null;
            }

            var sha = PinValue(working, Path.Combine(folder, docFile), field);
            if (sha is null)
            {
                return "missing";
            }

            if (!read.HasHead || read.ObjectOf($"{sha}^{{commit}}") is null)
            {
                return "missing";
            }

            var upstreamPath = prefix + upstreamFile;
            var atPin = read.ObjectOf($"{sha}:{upstreamPath}");
            var atHead = read.HeadBlobs.GetValueOrDefault(upstreamPath);
            return atPin != atHead ? "broken" : "holds";
        }

        var intentState = DocFacts("intent.md", intentHead, intentWorking);
        var specBase = DocFacts("spec.md", specHead, specWorking);
        var planBase = DocFacts("plan.md", planHead, planWorking);
        var documents = new DocumentsState(
            intentState,
            new PinnedDocumentState(specBase.Exists, specBase.InHead, specBase.HeadStatus, specBase.WorkingStatus, specBase.Uncommitted, CheckPin("spec.md", specWorking, "source_intent_commit", "intent.md")),
            new PinnedDocumentState(planBase.Exists, planBase.InHead, planBase.HeadStatus, planBase.WorkingStatus, planBase.Uncommitted, CheckPin("plan.md", planWorking, "source_spec_commit", "spec.md")));

        var (title, track) = ChangePlacer.TitleAndTrack(intentWorking, intentHead, Path.Combine(folder, "intent.md"), $"HEAD:{prefix}intent.md");

        int? scenarios = null;
        if (specWorking is not null)
        {
            try
            {
                var sr = _spec.Read(specWorking, Path.Combine(folder, "spec.md"));
                if (sr.Problems.Count == 0)
                {
                    scenarios = sr.Document.Requirements.Sum(r => r.Scenarios.Count);
                }
            }
            catch (CouldNotRunException)
            {
                // Unreadable spec -> null scenarios.
            }
        }

        static bool IsLog(string name) => name.EndsWith(".md", StringComparison.Ordinal) && name != "review.md" && !name.Contains('/');

        var headLogs = inHead
            .Where(p => p.StartsWith($"{prefix}build/", StringComparison.Ordinal))
            .Select(p => p[$"{prefix}build/".Length..])
            .Where(IsLog)
            .ToList();

        List<ParsedTask>? planTasks = null;
        if (planHead is not null)
        {
            try
            {
                planTasks = _progress.TasksOf(planHead, Path.Combine(folder, "plan.md"));
            }
            catch (CouldNotRunException)
            {
                planTasks = null;
            }
        }

        Dictionary<string, TaskWorkState>? tasks = null;
        ChangeSchedule? schedule = null;
        if (planTasks is not null)
        {
            var logged = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in headLogs)
            {
                var logText = HeadText($"build/{name}");
                if (logText is null)
                {
                    continue;
                }

                try
                {
                    foreach (var id in _progress.EntriesOf(logText, Path.Combine(folder, "build", name)))
                    {
                        logged.Add(id);
                    }
                }
                catch (CouldNotRunException)
                {
                    // Skip unreadable build log.
                }
            }

            tasks = new Dictionary<string, TaskWorkState>(StringComparer.Ordinal);
            foreach (var t in planTasks)
            {
                if (!tasks.TryGetValue(t.Repo, out var entry))
                {
                    entry = new TaskWorkState([], [], [], null, null, null);
                    tasks[t.Repo] = entry;
                }

                entry.All.Add(t.Id);
                (logged.Contains(t.Id) ? entry.Logged : entry.Unlogged).Add(t.Id);
            }

            schedule = ScheduleCalculator.Compute(planTasks, logged, _apps.IsSingleRepo(top));
            foreach (var repo in tasks.Keys.ToList())
            {
                var entry = tasks[repo];
                var (commits, untasked, uncommitted) = RepositoryWork(top, workspace, system, repo, entry.All, dirsCache, workCache);
                tasks[repo] = entry with { Commits = commits, Untasked = untasked, Uncommitted = uncommitted };
            }
        }

        var buildDir = Path.Combine(folder, "build");
        var workingLogs = PathHelpers.IsDirectory(buildDir)
            ? Directory.GetFiles(buildDir).Select(Path.GetFileName).Where(n => n is not null && IsLog(n)).Select(n => n!).ToList()
            : [];
        var buildLogs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in headLogs.Concat(workingLogs).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            var path = $"{prefix}build/{name}";
            var repo = name[..^3];
            if (!Uncommitted($"build/{name}"))
            {
                buildLogs[repo] = "clean";
            }
            else if (!inHead.Contains(path))
            {
                buildLogs[repo] = File.Exists(Path.Combine(buildDir, name)) ? "added-entries" : "other";
            }
            else
            {
                buildLogs[repo] = File.Exists(Path.Combine(buildDir, name)) && OnlyAddedLines(top, path) ? "added-entries" : "other";
            }
        }

        var reviewFile = Path.Combine(buildDir, "review.md");
        var reviewHead = HeadText("build/review.md");
        var committed = 0;
        if (reviewHead is not null)
        {
            try
            {
                committed = _review.Read(reviewHead, reviewFile).Document.Reviews.Count;
            }
            catch (CouldNotRunException)
            {
                committed = 0;
            }
        }

        var draft = "none";
        if (Uncommitted("build/review.md"))
        {
            draft = "other";
            var working = WorkingText("build/review.md");
            if (working is not null)
            {
                try
                {
                    var draftRead = _review.Read(working, reviewFile);
                    if (draftRead.Problems.Count == 0 && draftRead.Document.Reviews.Count == committed + 1)
                    {
                        var appended = reviewHead is null ? null : working.StartsWith(reviewHead, StringComparison.Ordinal) ? working[reviewHead.Length..] : null;
                        var nextRegex = new Regex($"^(\\r?\\n)*## Review {committed + 1}[ \\t]*\\r?(\\n|$)");
                        if (reviewHead is null || (appended is not null && nextRegex.IsMatch(appended)))
                        {
                            draft = "next-section";
                        }
                    }
                }
                catch (CouldNotRunException)
                {
                    // Stays "other".
                }
            }
        }

        FixStateResult? fix = null;
        if (track == "fix")
        {
            try
            {
                if (!PathHelpers.IsDirectory(folder))
                {
                    throw new CouldNotRunException($"{folder} does not exist");
                }

                fix = _fixState.ComputeFromInput(new FixInput(top, prefix, folder, read.HasHead, read.HeadText, read.HeadPaths, () => read.Reported));
            }
            catch (CouldNotRunException e)
            {
                problems.Add($"{change}: {e.Message}");
            }
        }

        Dictionary<string, AppState>? apps = _apps.IsSingleRepo(top) ? AppsOf(top, system, change, problems) : null;
        var reviewSummary = new ReviewSummary(committed, draft);
        var ready = ComputeReadiness(change, track, documents, scenarios, tasks, buildLogs, reviewSummary);

        return new ChangeStateItem(
            change,
            top,
            folder,
            inCopy,
            title,
            track,
            documents,
            scenarios,
            tasks,
            schedule,
            buildLogs,
            reviewSummary,
            fix,
            apps,
            ready,
            finished);
    }

    private Dictionary<string, AppState> AppsOf(string top, string system, string change, List<string> problems)
    {
        var folders = _apps.AppFolders(top, system);
        Dictionary<string, AppState> Unknown() => folders.ToDictionary(kv => kv.Key, kv => new AppState(kv.Value, false, null), StringComparer.Ordinal);
        var baseBranch = _git.BaseBranch(top);
        if (baseBranch is null || _git.CommitOf(top, "HEAD") is null)
        {
            return Unknown();
        }

        try
        {
            var touched = _apps.TouchedApps(top, system, top, baseBranch, "HEAD");
            return folders.ToDictionary(
                kv => kv.Key,
                kv => new AppState(kv.Value, touched.Contains(kv.Key), _apps.AppHead(top, "HEAD", kv.Value, baseBranch)),
                StringComparer.Ordinal);
        }
        catch (CouldNotRunException e)
        {
            problems.Add($"{change}: the apps' heads could not be worked out: {e.Message}");
            return Unknown();
        }
    }

    private (Dictionary<string, List<string>>? Commits, List<string>? Untasked, List<string>? Uncommitted) RepositoryWork(
        string top,
        string workspace,
        string system,
        string repo,
        List<string> ids,
        Dictionary<string, GitDirsInfo?> dirsCache,
        Dictionary<string, RepositoryFacts> workCache)
    {
        var single = _apps.IsSingleRepo(top);
        var folder = single ? _apps.AppFolders(top, system).GetValueOrDefault(repo) : null;
        if (single && (folder is null || !HasGit(top)))
        {
            return (null, null, null);
        }

        var listed = Path.Combine(workspace, repo);
        if (!single && !HasGit(listed))
        {
            return (null, null, null);
        }

        GitDirsInfo? DirsOf(string dir)
        {
            if (!dirsCache.TryGetValue(dir, out var info))
            {
                info = _git.GitDirs(dir);
                dirsCache[dir] = info;
            }

            return info;
        }

        var common = single ? null : DirsOf(listed)?.Common;
        var topCommon = single ? null : DirsOf(top)?.Common;
        var path = single || (common is not null && topCommon is not null && PathHelpers.PathsEqual(common, topCommon)) ? top : listed;
        var key = folder is null ? path : $"{path}\0{folder}";

        if (!workCache.TryGetValue(key, out var facts))
        {
            var uncommitted = folder is null ? OwnFiles.UncommittedFiles(_git, path) : _apps.UncommittedInApp(path, folder);
            var baseBranch = _git.BaseBranch(path);
            var baseRev = baseBranch is null ? null : _git.BaseRevision(path, baseBranch);
            var from = baseRev is null || _git.CommitOf(path, "HEAD") is null ? null : _git.MergeBase(path, baseRev, "HEAD");
            var trailers = from is null
                ? []
                : _git.CommitsWithTrailer(path, from, "HEAD", "Plan-Task", folder is null ? null : _apps.AppPathspec(folder));
            facts = new RepositoryFacts(uncommitted, from, trailers);
            workCache[key] = facts;
        }

        if (facts.From is null)
        {
            return (null, null, facts.Uncommitted);
        }

        var commits = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var untasked = new List<string>();
        foreach (var c in facts.Trailers)
        {
            if (c.Values.Count == 0)
            {
                untasked.Add(c.Sha);
            }

            foreach (var id in c.Values.Distinct(StringComparer.Ordinal))
            {
                if (commits.TryGetValue(id, out var list))
                {
                    list.Add(c.Sha);
                }
            }
        }

        return (commits, untasked, facts.Uncommitted);
    }

    private bool OnlyAddedLines(string top, string path)
    {
        var args = new List<string> { "diff", "HEAD" };
        args.AddRange(ProcessGitClient.DiffOptions);
        args.AddRange(["--no-color", "--unified=0", "--", path]);
        var r = _git.Run(top, args);
        if (!r.Ok)
        {
            throw new CouldNotRunException($"git diff failed in {top}: {(string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim()}");
        }

        var inHunks = false;
        foreach (var line in r.Stdout.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunks = true;
            }
            else if (inHunks && (line.StartsWith('-') || line.StartsWith('\\')))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadinessMap ComputeReadiness(
        string change,
        string track,
        DocumentsState d,
        int? scenarios,
        Dictionary<string, TaskWorkState>? tasks,
        Dictionary<string, string> buildLogs,
        ReviewSummary review)
    {
        string StatusOf(string doc) => doc switch
        {
            "intent" => d.Intent.WorkingStatus ?? "missing",
            "spec" => d.Spec.WorkingStatus ?? "missing",
            _ => d.Plan.WorkingStatus ?? "missing",
        };

        bool Accepted(string doc) => StatusOf(doc) == "accepted";
        bool Uncommitted(string doc) => doc switch
        {
            "intent" => d.Intent.Uncommitted,
            "spec" => d.Spec.Uncommitted,
            _ => d.Plan.Uncommitted,
        };

        string? HeadStatus(string doc) => doc switch
        {
            "intent" => d.Intent.HeadStatus,
            "spec" => d.Spec.HeadStatus,
            _ => d.Plan.HeadStatus,
        };

        static string FileOf(string doc) => $"{doc}.md";

        string? UncommittedAcceptance(params string[] docs)
        {
            var hit = docs.FirstOrDefault(x => Accepted(x) && Uncommitted(x));
            return hit is null ? null : $"`{FileOf(hit)}` has uncommitted changes. Commit the acceptance first, so history shows what was accepted.";
        }

        List<string> AcceptList(params string[] docs) =>
            docs.Where(x => HeadStatus(x) == "accepted" && Accepted(x) && !Uncommitted(x)).Select(FileOf).ToList();

        var light = track == "fix";
        var lightStop = $"`{change}` is on the light track; `fix --accept {change}` finishes or revises its documents.";
        var unloggedRepos = tasks is null ? [] : tasks.Where(kv => kv.Value.Unlogged.Count > 0).ToList();

        var specStop =
            (light ? lightStop : null) ??
            (!Accepted("intent") ? $"This intent is `{StatusOf("intent")}`. The product owner accepts it by changing its status to `accepted` and committing; the spec runs only after that." : null) ??
            UncommittedAcceptance("intent");

        var planStop =
            (light ? lightStop : null) ??
            (!Accepted("spec") ? $"This spec is `{StatusOf("spec")}`. The product owner accepts it by changing its status to `accepted` and committing." : null) ??
            (!Accepted("intent") ? $"The intent was reopened (`{StatusOf("intent")}`). The spec must be regenerated and accepted again before planning." : null) ??
            UncommittedAcceptance("intent", "spec") ??
            (d.Spec.Pin == "missing" ? "This spec does not record the intent it was written from. Rerun the spec skill; the product owner accepts it again before planning." : null) ??
            (d.Spec.Pin == "broken" ? "This spec was written from an earlier version of the intent. Rerun the spec skill; the product owner accepts it again before planning." : null) ??
            (scenarios is null or 0 ? "This spec has no Gherkin scenarios. Rerun the spec skill to regenerate it; the product owner accepts it again before planning." : null);

        var buildStop =
            (!Accepted("plan") ? $"This plan is `{StatusOf("plan")}`. The engineer accepts it by changing its status to `accepted` and committing." : null) ??
            (!Accepted("intent") ? $"`intent.md` was reopened (`{StatusOf("intent")}`). Rerun the skills after it, then accept the plan again." : null) ??
            (!Accepted("spec") ? $"`spec.md` was reopened (`{StatusOf("spec")}`). Rerun the skills after it, then accept the plan again." : null) ??
            UncommittedAcceptance("intent", "spec", "plan") ??
            (d.Spec.Pin != "holds" ? "The intent changed after this spec was written. Rerun the spec skill, then the plan skill." : null) ??
            (d.Plan.Pin != "holds" ? "This plan was not written from the spec that is accepted now. Rerun the plan skill." : null) ??
            (tasks is null ? "This plan cannot be read. Rerun the plan skill." : null);

        var dirtyLog = buildLogs.FirstOrDefault(kv => kv.Value != "clean");
        var reviewStop =
            buildStop ??
            (unloggedRepos.Count > 0 ? $"These tasks have no build log entry yet: {string.Join("; ", unloggedRepos.Select(kv => $"`{kv.Key}`: {string.Join(", ", kv.Value.Unlogged)}"))}." : null) ??
            (dirtyLog.Key is not null ? $"The build log for `{dirtyLog.Key}` has uncommitted changes; rerun the build for `{dirtyLog.Key}` to commit them." : null);

        var allLogged = tasks is not null && unloggedRepos.Count == 0;
        var uncommittedDoc = new[] { "intent", "spec", "plan" }.FirstOrDefault(Uncommitted);
        var rejectStop =
            (d.Intent.HeadStatus == "rejected" ? $"`{change}` is closed: its intent was rejected." : null) ??
            (buildLogs.Count > 0 || review.Committed > 0 || review.Draft != "none" ? $"`{change}` has build work (a build log or a review), so it is not rejected. The engineer settles that work first." : null) ??
            (uncommittedDoc is not null ? $"`{FileOf(uncommittedDoc)}` has uncommitted changes. Commit or discard them first, so the rejection does not carry them unread." : null);

        return new ReadinessMap(
            Spec: new SkillReadiness(specStop is null && (!d.Spec.Exists || !Accepted("spec") || d.Spec.Pin != "holds"), specStop, AcceptList("intent")),
            Plan: new SkillReadiness(planStop is null && (!d.Plan.Exists || d.Plan.WorkingStatus == "superseded"), planStop, AcceptList("intent", "spec")),
            Build: new SkillReadiness(buildStop is null && unloggedRepos.Count > 0, buildStop, AcceptList("intent", "spec", "plan")),
            Review: new SkillReadiness(reviewStop is null, reviewStop, AcceptList("intent", "spec", "plan")),
            Done: new SkillReadiness(review.Committed > 0 && allLogged, null, []),
            Reject: new SkillReadiness(false, rejectStop, []));
    }

    private static string? PinValue(string? text, string file, string field)
    {
        if (text is null)
        {
            return null;
        }

        var sha = (FrontMatter.TryGetScalar(text, file, field) ?? "").Trim();
        return sha.Length == 0 || sha == "pending acceptance" || sha.StartsWith('-') ? null : sha;
    }

    private static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static bool HasGit(string dir)
    {
        var gitPath = Path.Combine(dir, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
    }
}
