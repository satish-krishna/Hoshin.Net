using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Validates, auto-wraps bare JSONC front matter, writes upstream source pins, and commits change documents, reviews, and fixes via <see cref="IGitCommitter"/>.</summary>
public sealed class DocumentCommitter(
    IGitQuery gitQuery,
    IGitCommitter gitCommitter,
    IntentReader intentReader,
    SpecReader specReader,
    PlanReader planReader,
    ReviewReader reviewReader,
    FrontMatterEdit frontMatterEdit,
    CrossChecker crossChecker,
    IFileSystem fileSystem,
    TimeProvider timeProvider) : IDocumentCommitter
{
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IGitCommitter _gitCommitter = gitCommitter ?? throw new ArgumentNullException(nameof(gitCommitter));
    private readonly IntentReader _intentReader = intentReader ?? throw new ArgumentNullException(nameof(intentReader));
    private readonly SpecReader _specReader = specReader ?? throw new ArgumentNullException(nameof(specReader));
    private readonly PlanReader _planReader = planReader ?? throw new ArgumentNullException(nameof(planReader));
    private readonly ReviewReader _reviewReader = reviewReader ?? throw new ArgumentNullException(nameof(reviewReader));
    private readonly FrontMatterEdit _frontMatterEdit = frontMatterEdit ?? throw new ArgumentNullException(nameof(frontMatterEdit));
    private readonly CrossChecker _crossChecker = crossChecker ?? throw new ArgumentNullException(nameof(crossChecker));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public (IReadOnlyList<Problem> Problems, string? Commit) CommitDocument(WorkflowRepo repo, WorkflowState state, WorkflowDocument document)
    {
        var name = document.ToWireName();
        var path = state.Document(document).Path;
        var relative = Path.GetRelativePath(repo.Root, path).Replace('\\', '/');
        var rawText = ReadAndAutoWrap(path);

        List<Problem> problems = document switch
        {
            WorkflowDocument.Intent => _intentReader.Check(rawText, relative, state.Plan.Scope),
            WorkflowDocument.Spec => CheckSpecDocument(rawText, relative),
            WorkflowDocument.Plan => CheckPlanDocument(rawText, relative, state.Spec.Exists ? state.Spec.Path : null),
            _ => [new Problem(relative, 1, $"unknown document kind {document}")],
        };

        if (problems.Count > 0)
        {
            return (problems, null);
        }

        WriteSourcePin(repo, state, document, path);

        var commit = _gitCommitter.CommitPaths(
            repo.Root,
            [relative],
            [$"docs({state.Plan.Scope}): draft {name} {state.Plan.Change}", "Generated-By: hoshin"]);
        return commit.Ok
            ? ([], commit.Sha)
            : ([new Problem(relative, 1, $"could not commit: {TaskCommitHelpers.Squash(commit.Error ?? commit.Output)}")], null);
    }

    public (IReadOnlyList<Problem> Problems, string? Commit) CommitReview(WorkflowRepo repo, WorkflowState state)
    {
        var path = Path.Combine(repo.ChangeFolder, "build", "review.md");
        var relative = Path.GetRelativePath(repo.Root, path).Replace('\\', '/');
        if (!_fs.FileExists(path))
        {
            return ([new Problem(relative, 1, "build/review.md does not exist")], null);
        }

        var rawText = ReadAndAutoWrap(path);
        var doc = Doc.Split(rawText);
        if (FrontMatterCheck.Check(doc, relative) is { } shape)
        {
            return ([shape], null);
        }

        ReviewReadResult read;
        try
        {
            read = _reviewReader.Read(rawText, relative);
        }
        catch (CouldNotRunException e)
        {
            return ([new Problem(relative, 1, e.Message)], null);
        }

        if (read.Problems.Count > 0)
        {
            return (read.Problems, null);
        }

        var commit = _gitCommitter.CommitPaths(
            repo.Root,
            [relative],
            [$"docs({state.Plan.Scope}): review {state.Plan.Change}", "Generated-By: hoshin"]);
        if (!commit.Ok)
        {
            return ([new Problem(relative, 1, $"could not commit: {TaskCommitHelpers.Squash(commit.Error ?? commit.Output)}")], null);
        }

        var codeHead = state.EffectiveCodeHead;
        var latest = read.Document.Reviews.LastOrDefault();
        var reviewNum = latest?.Number ?? 1;
        var findingsCount = latest?.Findings.Count ?? 0;
        var highCount = latest?.Findings.Count(f => string.Equals(f.Severity, "high", StringComparison.OrdinalIgnoreCase)) ?? 0;
        repo.WithLedger(l =>
        {
            l.Reviews.Add(new WorkflowReviewRun(codeHead, reviewNum, findingsCount, highCount, _time.GetUtcNow()));
            return 0;
        });

        return ([], commit.Sha);
    }

    public (IReadOnlyList<Problem> Problems, string? Commit) CommitFixDocuments(WorkflowRepo repo, WorkflowState state)
    {
        var intentPath = Path.Combine(repo.ChangeFolder, "intent.md");
        var specPath = Path.Combine(repo.ChangeFolder, "spec.md");
        var planPath = Path.Combine(repo.ChangeFolder, "plan.md");

        var intentRel = Path.GetRelativePath(repo.Root, intentPath).Replace('\\', '/');
        var specRel = Path.GetRelativePath(repo.Root, specPath).Replace('\\', '/');
        var planRel = Path.GetRelativePath(repo.Root, planPath).Replace('\\', '/');

        var missing = CheckFixPresence((intentPath, intentRel, "intent.md"), (specPath, specRel, "spec.md"), (planPath, planRel, "plan.md"));
        if (missing.Count > 0)
        {
            return (missing, null);
        }

        var intentText = ReadAndAutoWrap(intentPath);
        var specText = ReadAndAutoWrap(specPath);
        var planText = ReadAndAutoWrap(planPath);

        var problems = new List<Problem>();
        problems.AddRange(_intentReader.Check(intentText, intentRel, state.Plan.Scope));
        problems.AddRange(CheckSpecDocument(specText, specRel));
        problems.AddRange(CheckPlanDocument(planText, planRel, specPath));
        problems.AddRange(CheckLightTrackBounds(planText, planRel));

        if (problems.Count > 0)
        {
            return (problems, null);
        }

        var commit = _gitCommitter.CommitPaths(
            repo.Root,
            [intentRel, specRel, planRel],
            [$"docs({state.Plan.Scope}): draft fix {state.Plan.Change}", "Generated-By: hoshin"]);
        return commit.Ok
            ? ([], commit.Sha)
            : ([new Problem(planRel, 1, $"could not commit fix draft: {TaskCommitHelpers.Squash(commit.Error ?? commit.Output)}")], null);
    }

    public string? AcceptDocument(WorkflowRepo repo, WorkflowState state, WorkflowDocument document)
    {
        var name = document.ToWireName();
        var path = state.Document(document).Path;
        var relative = Path.GetRelativePath(repo.Root, path).Replace('\\', '/');
        _frontMatterEdit.Set(path, "status", "accepted");
        var commit = _gitCommitter.CommitPaths(
            repo.Root,
            [relative],
            [$"docs({state.Plan.Scope}): accept {name} {state.Plan.Change}", "Generated-By: hoshin"]);
        return commit.Ok ? commit.Sha : null;
    }

    private string ReadAndAutoWrap(string path)
    {
        var rawText = _fs.ReadAllText(path);
        var wrapped = BareFrontMatterWrapper.TryAutoWrap(rawText);
        if (wrapped is null)
        {
            return rawText;
        }

        _fs.WriteAllTextAtomic(path, wrapped);
        return wrapped;
    }

    private void WriteSourcePin(WorkflowRepo repo, WorkflowState state, WorkflowDocument document, string path)
    {
        if (document == WorkflowDocument.Spec && state.Intent.InHead)
        {
            var intentRel = Path.GetRelativePath(repo.Root, state.Intent.Path).Replace('\\', '/');
            var sha = _gitQuery.LastCommitForPath(repo.Root, intentRel);
            if (!string.IsNullOrEmpty(sha))
            {
                _frontMatterEdit.Set(path, "source_intent_commit", sha);
            }
        }
        else if (document == WorkflowDocument.Plan && state.Spec.InHead)
        {
            var specRel = Path.GetRelativePath(repo.Root, state.Spec.Path).Replace('\\', '/');
            var sha = _gitQuery.LastCommitForPath(repo.Root, specRel);
            if (!string.IsNullOrEmpty(sha))
            {
                _frontMatterEdit.Set(path, "source_spec_commit", sha);
            }
        }
    }

    private List<Problem> CheckFixPresence(params (string Full, string Rel, string Name)[] required)
    {
        var problems = new List<Problem>();
        foreach (var (full, rel, name) in required)
        {
            if (!_fs.FileExists(full))
            {
                problems.Add(new Problem(rel, 1, $"light-track fix requires {name}"));
            }
        }

        return problems;
    }

    private List<Problem> CheckLightTrackBounds(string planText, string planRel)
    {
        var problems = new List<Problem>();
        try
        {
            var planRead = _planReader.Read(planText, planRel);
            if (planRead.Document.Tasks.Count > 3)
            {
                problems.Add(new Problem(planRel, planRead.Document.Tasks[3].Line, "light-track fix allows at most 3 tasks; use the full intent pipeline for larger changes"));
            }

            var distinctRepos = planRead.Document.Tasks
                .Select(t => t.Repo)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (distinctRepos.Count > 1)
            {
                problems.Add(new Problem(planRel, 1, $"light-track fix must touch a single repository, found: {string.Join(", ", distinctRepos)}"));
            }
        }
        catch (CouldNotRunException e)
        {
            problems.Add(new Problem(planRel, 1, e.Message));
        }

        return problems;
    }

    private List<Problem> CheckSpecDocument(string text, string file)
    {
        var doc = Doc.Split(text);
        if (FrontMatterCheck.Check(doc, file) is { } shape)
        {
            return [shape];
        }

        try
        {
            var res = _specReader.Read(text, file);
            var problems = new List<Problem>(res.Problems);
            foreach (var r in res.Document.Requirements)
            {
                if (r.From.Count == 0 && !string.IsNullOrEmpty(r.Id))
                {
                    problems.Add(new Problem(file, r.Line, $"requirement {r.Id} has no @from:<source> tag"));
                }
            }

            return problems;
        }
        catch (CouldNotRunException e)
        {
            return [new Problem(file, 1, e.Message)];
        }
    }

    private List<Problem> CheckPlanDocument(string text, string file, string? specPath = null)
    {
        var doc = Doc.Split(text);
        if (FrontMatterCheck.Check(doc, file) is { } shape)
        {
            return [shape];
        }

        try
        {
            var planRead = _planReader.Read(text, file);
            var problems = new List<Problem>(planRead.Problems);
            if (specPath is not null && _fs.FileExists(specPath))
            {
                problems.AddRange(CheckCrossSpecPlan(specPath, file, planRead));
            }

            return problems;
        }
        catch (CouldNotRunException e)
        {
            return [new Problem(file, 1, e.Message)];
        }
    }

    private List<Problem> CheckCrossSpecPlan(string specPath, string planFile, PlanReadResult planRead)
    {
        try
        {
            var specRead = _specReader.Read(_fs.ReadAllText(specPath), specPath);
            if (specRead.Problems.Count == 0)
            {
                return _crossChecker.CheckChange([
                    new ChangeDoc(specPath, specRead),
                    new ChangeDoc(planFile, planRead),
                ]);
            }
        }
        catch (Exception e) when (e is CouldNotRunException or IOException)
        {
            // Spec unreadable; plan-only problems still reported.
        }

        return [];
    }
}
