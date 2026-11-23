using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// A git repository hosting a workflow run, together with its change folder and <c>.git/hoshin/</c> ledger/step paths.
/// Performs zero direct process execution; every Git query goes through narrow <see cref="IGitQuery"/> / <see cref="IGitStatus"/> interfaces.
/// </summary>
public sealed class WorkflowRepo(
    string root,
    string gitCommonDir,
    string changeFolder,
    IGitQuery gitQuery,
    IGitStatus gitStatus,
    IFileSystem fileSystem)
{
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IGitStatus _gitStatus = gitStatus ?? throw new ArgumentNullException(nameof(gitStatus));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public string Root { get; } = Path.GetFullPath(root);

    public string HoshinDir { get; } = Path.Combine(Path.GetFullPath(gitCommonDir), "hoshin");

    public string ChangeFolder { get; } = Path.GetFullPath(changeFolder);

    public IGitQuery GitQuery => _gitQuery;

    public IGitStatus GitStatus => _gitStatus;

    public string ChangeKey => ChangeLedgerPaths.ResolveChangeKey(ChangeFolder);

    public string LedgerPath => ChangeLedgerPaths.LedgerPath(HoshinDir, ChangeKey);

    public string StepPath => Path.Combine(HoshinDir, "step.json");

    public string RunMarkerPath => Path.Combine(HoshinDir, "run.json");

    public string RunLockPath => Path.Combine(HoshinDir, "run.lock");

    public string PendingContextPath => Path.Combine(HoshinDir, "pending-context.txt");

    public string HooksLogPath => Path.Combine(HoshinDir, "hooks.jsonl");

    public string RedWorktreePath => Path.Combine(HoshinDir, "worktrees", "red-check");

    public static WorkflowRepo? Find(
        string directory,
        IGitWorktrees gitWorktrees,
        IGitQuery gitQuery,
        IGitStatus gitStatus,
        IFileSystem fileSystem,
        string? explicitChangeFolder = null)
    {
        ArgumentNullException.ThrowIfNull(gitWorktrees);
        ArgumentNullException.ThrowIfNull(gitQuery);
        ArgumentNullException.ThrowIfNull(gitStatus);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!fileSystem.DirectoryExists(directory))
        {
            return null;
        }

        var top = gitWorktrees.TopLevel(directory);
        var dirs = top is null ? null : gitWorktrees.GitDirs(top);
        if (top is null || dirs is null)
        {
            return null;
        }

        var root = Path.GetFullPath(top);
        var commonDir = Path.GetFullPath(dirs.Common);
        var changeFolder = ChangeFolderResolver.Resolve(root, commonDir, explicitChangeFolder, fileSystem);
        return new WorkflowRepo(root, commonDir, changeFolder, gitQuery, gitStatus, fileSystem);
    }

    public string Head() => _gitQuery.CommitOf(Root, "HEAD") ?? "";

    public string CodeHead()
    {
        var reviewRel = Path.GetRelativePath(Root, Path.Combine(ChangeFolder, "build", "review.md")).Replace('\\', '/');
        var sha = _gitQuery.CodeHead(Root, reviewRel);
        return !string.IsNullOrWhiteSpace(sha) ? sha : Head();
    }

    public WorkflowPlan? ReadPlan(PlanReader planReader) =>
        WorkflowPlanLoader.Load(this, planReader, _fs);

    public WorkflowDocState DocumentStatus(string fileName)
    {
        var path = Path.Combine(ChangeFolder, fileName);
        var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
        var exists = _fs.FileExists(path);
        var headContent = _gitQuery.ShowFile(Root, "HEAD", relative);
        var inHead = headContent is not null;
        var uncommitted = _gitStatus.ChangedPaths(Root, [relative]).Count > 0;
        return new WorkflowDocState(
            path,
            exists,
            inHead,
            uncommitted,
            exists ? StatusOf(_fs.ReadAllText(path)) : null,
            inHead ? StatusOf(headContent!) : null);
    }

    private static string? StatusOf(string text)
    {
        try
        {
            return Jsonc.String(FrontMatter.Parse(text, "document").Data, "status");
        }
        catch (CouldNotRunException)
        {
            return null;
        }
    }

    public WorkflowLedger ReadLedger() => WorkflowLedgerStore.ReadLedger(this, _fs);

    public void WriteLedger(WorkflowLedger ledger) => WorkflowLedgerStore.WriteLedger(this, _fs, ledger);

    public CurrentStep? ReadStep() => WorkflowLedgerStore.ReadStep(this, _fs);

    public void WriteStep(CurrentStep step) => WorkflowLedgerStore.WriteStep(this, _fs, step);

    public RunMarker? ReadRunMarker() => WorkflowLedgerStore.ReadRunMarker(this, _fs);

    public void WriteRunMarker(RunMarker marker) => WorkflowLedgerStore.WriteRunMarker(this, _fs, marker);

    public T WithLedger<T>(Func<WorkflowLedger, T> update) => WorkflowLedgerStore.WithLedger(this, _fs, update);

    public FileStream AcquireRunLock(TimeSpan? timeout = null) => WorkflowLedgerStore.AcquireRunLock(this, timeout);

    /// <summary>Checks whether a repository-relative path matches any configured test glob pattern or explicit task test file.</summary>
    public static bool IsTestPath(string relativePath, IReadOnlyList<string>? testPatterns = null, IReadOnlyList<string>? taskTestFiles = null) =>
        TestPathClassifier.IsTestPath(relativePath, testPatterns, taskTestFiles);

    /// <summary>Extracts verified test file paths referenced inside a task's <c>test</c> argv (e.g. <c>node --test test/t1.test.mjs</c>).</summary>
    public static List<string> ExtractTestFilesFromArgv(string[] argv, string? repoRoot = null, IReadOnlyList<string>? testPatterns = null) =>
        TestPathClassifier.ExtractTestFilesFromArgv(argv, repoRoot, testPatterns);
}
