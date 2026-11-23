using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// A git repository hosting a workflow run, together with its change folder and <c>.git/hoshin/</c> ledger/step paths.
/// Delegates resolution, plan loading, ledger persistence, and test-path classification to dedicated helpers.
/// </summary>
public sealed class WorkflowRepo
{
    private readonly IAllowListedCommandRunner _runner;
    private readonly IFileSystem _fs;

    private WorkflowRepo(string root, string gitCommonDir, string changeFolder, IAllowListedCommandRunner runner, IFileSystem fileSystem)
    {
        Root = Path.GetFullPath(root);
        HoshinDir = Path.Combine(Path.GetFullPath(gitCommonDir), "hoshin");
        ChangeFolder = Path.GetFullPath(changeFolder);
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public string Root { get; }

    public string HoshinDir { get; }

    public string ChangeFolder { get; }

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
        IAllowListedCommandRunner runner,
        IFileSystem fileSystem,
        string? explicitChangeFolder = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!fileSystem.DirectoryExists(directory))
        {
            return null;
        }

        var top = GitIn(runner, directory, "rev-parse", "--show-toplevel");
        var common = GitIn(runner, directory, "rev-parse", "--path-format=absolute", "--git-common-dir");
        if (!top.Ok || !common.Ok)
        {
            return null;
        }

        var root = Path.GetFullPath(top.Output.Trim());
        var commonDir = Path.GetFullPath(common.Output.Trim());
        var changeFolder = ChangeFolderResolver.Resolve(root, commonDir, explicitChangeFolder, fileSystem);
        return new WorkflowRepo(root, commonDir, changeFolder, runner, fileSystem);
    }

    public AllowListedCommandResult Git(params string[] args) => GitIn(_runner, Root, args);

    public AllowListedCommandResult GitWithEnv(IReadOnlyDictionary<string, string>? extraEnv, params string[] args) =>
        _runner.Run(Root, ["git", .. args], TimeSpan.FromMinutes(2), extraEnv);

    public static AllowListedCommandResult GitIn(IAllowListedCommandRunner runner, string directory, params string[] args) =>
        runner.Run(directory, ["git", .. args], TimeSpan.FromMinutes(2));

    public string Head() => Git("rev-parse", "HEAD").Output.Trim();

    public string CodeHead()
    {
        var reviewRel = Path.GetRelativePath(Root, Path.Combine(ChangeFolder, "build", "review.md")).Replace('\\', '/');
        var res = Git("log", "-1", "--format=%H", "--", ".", $":(exclude){reviewRel}");
        var sha = res.Ok ? res.Output.Trim() : "";
        return sha.Length > 0 ? sha : Head();
    }

    public WorkflowPlan? ReadPlan(PlanReader planReader) =>
        WorkflowPlanLoader.Load(this, planReader, _fs);

    public WorkflowDocState DocumentStatus(string fileName)
    {
        var path = Path.Combine(ChangeFolder, fileName);
        var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
        var exists = _fs.FileExists(path);
        var head = Git("show", $"HEAD:{relative}");
        var uncommitted = Git("status", "--porcelain", "--", relative).Output.Trim().Length > 0;
        return new WorkflowDocState(
            path,
            exists,
            head.Ok,
            uncommitted,
            exists ? StatusOf(_fs.ReadAllText(path)) : null,
            head.Ok ? StatusOf(head.Output) : null);
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
