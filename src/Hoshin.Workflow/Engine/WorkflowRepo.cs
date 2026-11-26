using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// A git repository hosting a workflow run, together with its change folder and unified <see cref="RepoLedger"/> /
/// <c>.git/hoshin/</c> step and run-marker paths.
/// Performs zero direct process execution; every Git query goes through narrow <see cref="IGitQuery"/> / <see cref="IGitStatus"/> interfaces.
/// </summary>
public sealed class WorkflowRepo
{
    private readonly IGitQuery _gitQuery;
    private readonly IGitStatus _gitStatus;
    private readonly IFileSystem _fs;
    private readonly LedgerStore _ledgerStore;
    private readonly WorkflowLedgerCoordinates _coords;

    public WorkflowRepo(
        string root,
        string gitCommonDir,
        string changeFolder,
        IGitQuery gitQuery,
        IGitStatus gitStatus,
        IFileSystem fileSystem,
        LedgerStore ledgerStore)
    {
        _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
        _gitStatus = gitStatus ?? throw new ArgumentNullException(nameof(gitStatus));
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));

        Root = Path.GetFullPath(root);
        HoshinDir = Path.Combine(Path.GetFullPath(gitCommonDir), "hoshin");
        ChangeFolder = Path.GetFullPath(changeFolder);
        RedWorktreePath = Path.Combine(HoshinDir, "worktrees", "red-check");
        _coords = ChangeLedgerPaths.ResolveCoordinates(Root, ChangeFolder, _fs);
    }

    private WorkflowRepo(
        string root,
        string hoshinDir,
        string changeFolder,
        string redWorktreePath,
        WorkflowLedgerCoordinates coords,
        IGitQuery gitQuery,
        IGitStatus gitStatus,
        IFileSystem fileSystem,
        LedgerStore ledgerStore)
    {
        _gitQuery = gitQuery;
        _gitStatus = gitStatus;
        _fs = fileSystem;
        _ledgerStore = ledgerStore;
        Root = Path.GetFullPath(root);
        HoshinDir = Path.GetFullPath(hoshinDir);
        ChangeFolder = Path.GetFullPath(changeFolder);
        RedWorktreePath = Path.GetFullPath(redWorktreePath);
        _coords = coords;
    }

    public WorkflowRepo ForTarget(string targetRoot, string targetGitCommonDir, string repoKey) =>
        new(
            targetRoot,
            HoshinDir,
            ChangeFolder,
            Path.Combine(Path.GetFullPath(targetGitCommonDir), "hoshin", "worktrees", "red-check"),
            _coords with { Repo = repoKey },
            _gitQuery,
            _gitStatus,
            _fs,
            _ledgerStore);

    public string Root { get; }

    public string HoshinDir { get; }

    public string ChangeFolder { get; }

    public IGitQuery GitQuery => _gitQuery;

    public IGitStatus GitStatus => _gitStatus;

    public string SystemKey => _coords.System;

    public string ChangeKey => _coords.Change;

    public string RepoKey => _coords.Repo;

    public string LedgerFolder => Path.Combine(HoshinDir, SystemKey, ChangeKey);

    public string LedgerPath => Path.Combine(LedgerFolder, $"{RepoKey}.json");

    public string StepPath => Path.Combine(HoshinDir, "step.json");

    public string RunMarkerPath => Path.Combine(HoshinDir, "run.json");

    public string RunLockPath => Path.Combine(HoshinDir, "run.lock");

    public string PendingContextPath => Path.Combine(HoshinDir, "pending-context.txt");

    public string HooksLogPath => Path.Combine(HoshinDir, "hooks.jsonl");

    public string RedWorktreePath { get; }

    public static WorkflowRepo? Find(
        string directory,
        IGitWorktrees gitWorktrees,
        IGitQuery gitQuery,
        IGitStatus gitStatus,
        IFileSystem fileSystem,
        LedgerStore ledgerStore,
        string? explicitChangeFolder = null)
    {
        ArgumentNullException.ThrowIfNull(gitWorktrees);
        ArgumentNullException.ThrowIfNull(gitQuery);
        ArgumentNullException.ThrowIfNull(gitStatus);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(ledgerStore);
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
        root = ResolveCheckoutForChangeFolder(root, commonDir, changeFolder, gitWorktrees, fileSystem);
        return new WorkflowRepo(root, commonDir, changeFolder, gitQuery, gitStatus, fileSystem, ledgerStore);
    }

    private static string ResolveCheckoutForChangeFolder(
        string fallbackRoot,
        string commonDir,
        string changeFolder,
        IGitWorktrees gitWorktrees,
        IFileSystem fs)
    {
        var probe = Path.GetFullPath(changeFolder);
        while (!string.IsNullOrEmpty(probe) && !fs.DirectoryExists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }

        if (string.IsNullOrEmpty(probe))
        {
            return fallbackRoot;
        }

        var top = gitWorktrees.TopLevel(probe);
        var dirs = top is null ? null : gitWorktrees.GitDirs(top);
        return top is not null && dirs is not null && PathHelpers.PathsEqual(dirs.Common, commonDir)
            ? Path.GetFullPath(top)
            : fallbackRoot;
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

    public RepoLedger ReadLedger()
    {
        var migrated = ChangeLedgerPaths.TryMigrateLegacyLedger(
            HoshinDir,
            Root,
            ChangeFolder,
            _coords,
            ReadRunMarker(),
            _fs,
            _ledgerStore);
        if (migrated is not null)
        {
            return migrated;
        }

        return _ledgerStore.Read(LedgerFolder, RepoKey) ?? LedgerStore.Empty(SystemKey, ChangeKey, RepoKey, Root);
    }

    public void WriteLedger(RepoLedger ledger) =>
        _ledgerStore.Write(LedgerFolder, ledger);

    public RepoLedger UpdateLedger(Func<RepoLedger, RepoLedger> update) =>
        _ledgerStore.Update(
            LedgerFolder,
            RepoKey,
            () => ChangeLedgerPaths.TryMigrateLegacyLedger(
                      HoshinDir,
                      Root,
                      ChangeFolder,
                      _coords,
                      ReadRunMarker(),
                      _fs,
                      _ledgerStore)
                  ?? LedgerStore.Empty(SystemKey, ChangeKey, RepoKey, Root),
            update);

    public CurrentStep? ReadStep() =>
        _fs.FileExists(StepPath)
            ? JsonSerializer.Deserialize(_fs.ReadAllText(StepPath), WorkflowJsonContext.Default.CurrentStep)
            : null;

    public void WriteStep(CurrentStep step) =>
        _fs.WriteAllTextAtomic(StepPath, JsonSerializer.Serialize(step, WorkflowJsonContext.Default.CurrentStep));

    public RunMarker? ReadRunMarker() =>
        _fs.FileExists(RunMarkerPath)
            ? JsonSerializer.Deserialize(_fs.ReadAllText(RunMarkerPath), WorkflowJsonContext.Default.RunMarker)
            : null;

    public void WriteRunMarker(RunMarker marker) =>
        _fs.WriteAllTextAtomic(RunMarkerPath, JsonSerializer.Serialize(marker, WorkflowJsonContext.Default.RunMarker));

    public FileStream AcquireRunLock(TimeSpan? timeout = null) =>
        FileLock.Acquire(RunLockPath, timeout ?? TimeSpan.FromMinutes(10));

    /// <summary>Checks whether a repository-relative path matches any configured test glob pattern or explicit task test file.</summary>
    public static bool IsTestPath(string relativePath, IReadOnlyList<string>? testPatterns = null, IReadOnlyList<string>? taskTestFiles = null) =>
        TestPathClassifier.IsTestPath(relativePath, testPatterns, taskTestFiles);

    /// <summary>Extracts verified test file paths referenced inside a task's <c>test</c> argv (e.g. <c>node --test test/t1.test.mjs</c>).</summary>
    public static List<string> ExtractTestFilesFromArgv(string[] argv, string? repoRoot = null, IReadOnlyList<string>? testPatterns = null) =>
        TestPathClassifier.ExtractTestFilesFromArgv(argv, repoRoot, testPatterns);
}
