using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// A git repository hosting a workflow run, together with its change folder and <c>.git/hoshin/</c> ledger/step files.
/// Supports both standalone <c>.hoshin/</c> change folders (test/toy layouts) and standard <c>intent/&lt;system&gt;/&lt;change&gt;/</c> folders.
/// </summary>
public sealed class WorkflowRepo
{
    private readonly IAllowListedCommandRunner _runner;

    private WorkflowRepo(string root, string gitCommonDir, string changeFolder, IAllowListedCommandRunner runner)
    {
        Root = Path.GetFullPath(root);
        HoshinDir = Path.Combine(Path.GetFullPath(gitCommonDir), "hoshin");
        ChangeFolder = Path.GetFullPath(changeFolder);
        _runner = runner;
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

    public static WorkflowRepo? Find(string directory, string? explicitChangeFolder = null, IAllowListedCommandRunner? runner = null)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var r = runner ?? AllowListedCommandRunner.Default;
        var top = GitIn(r, directory, "rev-parse", "--show-toplevel");
        var common = GitIn(r, directory, "rev-parse", "--path-format=absolute", "--git-common-dir");
        if (!top.Ok || !common.Ok)
        {
            return null;
        }

        var root = Path.GetFullPath(top.Output.Trim());
        var commonDir = Path.GetFullPath(common.Output.Trim());
        var changeFolder = ResolveChangeFolder(root, commonDir, explicitChangeFolder);
        return new WorkflowRepo(root, commonDir, changeFolder, r);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: fix in T43")]
    private static string ResolveChangeFolder(string root, string commonDir, string? explicitChangeFolder)
    {
        if (!string.IsNullOrWhiteSpace(explicitChangeFolder))
        {
            return Path.IsPathRooted(explicitChangeFolder)
                ? explicitChangeFolder
                : Path.Combine(root, explicitChangeFolder);
        }

        // Check existing run marker first so multi-turn sessions stay pinned to the active change.
        var runPath = Path.Combine(commonDir, "hoshin", "run.json");
        if (File.Exists(runPath))
        {
            try
            {
                var marker = JsonSerializer.Deserialize(File.ReadAllText(runPath), WorkflowJsonContext.Default.RunMarker);
                if (!string.IsNullOrWhiteSpace(marker?.ChangeFolder) && Directory.Exists(marker.ChangeFolder))
                {
                    return marker.ChangeFolder;
                }
            }
            catch
            {
                // Ignore corrupt marker.
            }
        }

        // Direct `.hoshin/` change folder in repo root (used by standalone/single-change layouts).
        var dotHoshin = Path.Combine(root, ".hoshin");
        if (File.Exists(Path.Combine(dotHoshin, "change.jsonc")) || File.Exists(Path.Combine(dotHoshin, "plan.md")) || File.Exists(Path.Combine(dotHoshin, "intent.md")))
        {
            return dotHoshin;
        }

        // Standard `intent/<system>/<change>/` layout: pick the sole or most recent change folder with documents.
        var intentRoot = Path.Combine(root, "intent");
        if (Directory.Exists(intentRoot))
        {
            var candidates = new List<string>();
            foreach (var sysDir in Directory.GetDirectories(intentRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var sysName = Path.GetFileName(sysDir);
                if (sysName.Equals("hooks", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var chDir in Directory.GetDirectories(sysDir).OrderByDescending(d => d, StringComparer.Ordinal))
                {
                    var chName = Path.GetFileName(chDir);
                    if (chName is "context" or "hooks")
                    {
                        continue;
                    }

                    if (File.Exists(Path.Combine(chDir, "change.jsonc")) ||
                        File.Exists(Path.Combine(chDir, "plan.md")) ||
                        File.Exists(Path.Combine(chDir, "intent.md")) ||
                        File.Exists(Path.Combine(chDir, "spec.md")))
                    {
                        candidates.Add(chDir);
                    }
                }
            }

            if (candidates.Count > 0)
            {
                return candidates[0];
            }
        }

        return dotHoshin;
    }

    public AllowListedCommandResult Git(params string[] args) => GitIn(_runner, Root, args);

    public AllowListedCommandResult GitWithEnv(IReadOnlyDictionary<string, string>? extraEnv, params string[] args) =>
        _runner.Run(Root, ["git", .. args], TimeSpan.FromMinutes(2), extraEnv);

    public static AllowListedCommandResult GitIn(IAllowListedCommandRunner runner, string directory, params string[] args) =>
        runner.Run(directory, ["git", .. args], TimeSpan.FromMinutes(2));

    public string Head() => Git("rev-parse", "HEAD").Output.Trim();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: fix in T43")]
    public WorkflowPlan? ReadPlan(PlanReader? planReader = null)
    {
        var reader = planReader ?? new PlanReader();
        var testPatterns = ReadTestPatternsFromMarker();
        var planPath = Path.Combine(ChangeFolder, "plan.md");
        var changePath = Path.Combine(ChangeFolder, "change.jsonc");

        if (!File.Exists(planPath))
        {
            return LoadPlanMetadataOnly(changePath, testPatterns);
        }

        var planDocStatus = DocumentStatus("plan.md");
        PlanReadResult? read = null;
        try
        {
            read = reader.Read(File.ReadAllText(planPath), "plan.md");
        }
        catch (CouldNotRunException) when (planDocStatus.Uncommitted || !planDocStatus.InHead)
        {
            // An uncommitted draft plan (e.g. bare JSONC front matter during WritePlan/Fix) is validated at Stop.
            return LoadPlanMetadataOnly(changePath, testPatterns);
        }

        if (read.Problems.Count > 0)
        {
            if (planDocStatus.Uncommitted || !planDocStatus.InHead)
            {
                return LoadPlanMetadataOnly(changePath, testPatterns);
            }

            throw new CouldNotRunException("plan.md has problems: " + string.Join("; ", read.Problems.Select(p => $"line {p.Line}: {p.Message}")));
        }

        JsonElement changeObj = default;
        var hasChange = File.Exists(changePath);
        if (hasChange)
        {
            changeObj = ParseJsoncObject(changePath);
        }

        var scope = (hasChange ? Jsonc.String(changeObj, "scope") : null)
            ?? Jsonc.String(read.FrontMatter, "scope")
            ?? Jsonc.String(read.FrontMatter, "system")
            ?? throw new CouldNotRunException("change.jsonc lacks scope");

        var change = (hasChange ? Jsonc.String(changeObj, "change") : null) ?? Path.GetFileName(ChangeFolder);
        var baseBranch = (hasChange ? Jsonc.String(changeObj, "base") : null) ?? "main";
        var branch = hasChange ? Jsonc.String(changeObj, "branch") : null;
        var workType = (hasChange ? Jsonc.String(changeObj, "type") : null) ?? "feat";

        var tasks = new List<WorkflowTask>();
        foreach (var t in read.Document.Tasks)
        {
            if (t.Test is null && (planDocStatus.Uncommitted || !planDocStatus.InHead))
            {
                return LoadPlanMetadataOnly(changePath, testPatterns);
            }

            tasks.Add(new WorkflowTask(
                t.Id,
                t.Name,
                string.Join("\n\n", t.Sections.Select(s => s.Body)),
                t.Test ?? throw new CouldNotRunException($"plan.md: {t.Id} has no test command"),
                t.Compile,
                t.Files));
        }

        var systemName = Jsonc.String(read.FrontMatter, "system") ?? scope;
        return new WorkflowPlan(
            Change: change,
            Scope: scope,
            Base: baseBranch,
            Tasks: tasks,
            Checks: BaseBranchChecksReader.Read(this, baseBranch, systemName),
            HasPlan: true,
            Branch: branch,
            WorkType: workType,
            TestPatterns: testPatterns);
    }

    private WorkflowPlan? LoadPlanMetadataOnly(string changePath, List<string>? testPatterns)
    {
        if (!File.Exists(changePath))
        {
            return null;
        }

        var only = ParseJsoncObject(changePath);
        var changeName = Jsonc.String(only, "change") ?? Path.GetFileName(ChangeFolder);
        var scope = Jsonc.String(only, "scope") ?? throw new CouldNotRunException("change.jsonc lacks scope");
        var baseBranch = Jsonc.String(only, "base") ?? "main";
        return new WorkflowPlan(
            Change: changeName,
            Scope: scope,
            Base: baseBranch,
            Tasks: [],
            Checks: BaseBranchChecksReader.Read(this, baseBranch, scope),
            HasPlan: false,
            Branch: Jsonc.String(only, "branch"),
            WorkType: Jsonc.String(only, "type") ?? "feat",
            TestPatterns: testPatterns);
    }

    public WorkflowDocState DocumentStatus(string fileName)
    {
        var path = Path.Combine(ChangeFolder, fileName);
        var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
        var exists = File.Exists(path);
        var head = Git("show", $"HEAD:{relative}");
        var uncommitted = Git("status", "--porcelain", "--", relative).Output.Trim().Length > 0;
        return new WorkflowDocState(
            path,
            exists,
            head.Ok,
            uncommitted,
            exists ? StatusOf(File.ReadAllText(path)) : null,
            head.Ok ? StatusOf(head.Output) : null);
    }

    private static string? StatusOf(string text)
    {
        try
        {
            return Jsonc.String(FrontMatter.Parse(text, "document").Data, "status");
        }
        catch
        {
            return null;
        }
    }

    private List<string>? ReadTestPatternsFromMarker()
    {
        var markerPath = Path.Combine(Root, MarkerConstants.FileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        if (!Jsonc.TryParse(File.ReadAllText(markerPath), out var raw, out _) || raw is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }

        var patterns = Jsonc.Strings(obj, "testPatterns");
        return patterns.Count > 0 ? patterns : null;
    }

    private static JsonElement ParseJsoncObject(string path)
    {
        if (!Jsonc.TryParse(File.ReadAllText(path), out var value, out var error))
        {
            throw new CouldNotRunException($"{Path.GetFileName(path)} is not valid JSONC: {error}");
        }

        return value is { ValueKind: JsonValueKind.Object } obj
            ? obj
            : throw new CouldNotRunException($"{Path.GetFileName(path)} is not a set of fields");
    }

    public WorkflowLedger ReadLedger()
    {
        ChangeLedgerPaths.TryMigrateLegacyLedger(HoshinDir, LedgerPath, ReadRunMarker());
        return File.Exists(LedgerPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(LedgerPath), WorkflowJsonContext.Default.WorkflowLedger) ?? new()
            : new();
    }

    public void WriteLedger(WorkflowLedger ledger) =>
        WriteAtomic(LedgerPath, JsonSerializer.Serialize(ledger, WorkflowJsonContext.Default.WorkflowLedger));

    public CurrentStep? ReadStep() =>
        File.Exists(StepPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(StepPath), WorkflowJsonContext.Default.CurrentStep)
            : null;

    public void WriteStep(CurrentStep step) =>
        WriteAtomic(StepPath, JsonSerializer.Serialize(step, WorkflowJsonContext.Default.CurrentStep));

    public RunMarker? ReadRunMarker() =>
        File.Exists(RunMarkerPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(RunMarkerPath), WorkflowJsonContext.Default.RunMarker)
            : null;

    public void WriteRunMarker(RunMarker marker) =>
        WriteAtomic(RunMarkerPath, JsonSerializer.Serialize(marker, WorkflowJsonContext.Default.RunMarker));

    public T WithLedger<T>(Func<WorkflowLedger, T> update)
    {
        Directory.CreateDirectory(HoshinDir);
        using var gate = AcquireLock();
        var ledger = ReadLedger();
        var result = update(ledger);
        WriteLedger(ledger);
        return result;
    }

    public FileStream AcquireRunLock(TimeSpan? timeout = null) =>
        FileLock.Acquire(RunLockPath, timeout ?? TimeSpan.FromMinutes(10));

    private FileStream AcquireLock() =>
        FileLock.Acquire(Path.Combine(HoshinDir, "workflow-ledger.lock"), TimeSpan.FromSeconds(5));

    private void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? HoshinDir);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Checks whether a repository-relative path matches any configured test glob pattern or explicit task test file.</summary>
    public static bool IsTestPath(string relativePath, IReadOnlyList<string>? testPatterns = null, IReadOnlyList<string>? taskTestFiles = null) =>
        TestPathClassifier.IsTestPath(relativePath, testPatterns, taskTestFiles);

    /// <summary>Extracts verified test file paths referenced inside a task's <c>test</c> argv (e.g. <c>node --test test/t1.test.mjs</c>).</summary>
    public static List<string> ExtractTestFilesFromArgv(string[] argv, string? repoRoot = null, IReadOnlyList<string>? testPatterns = null) =>
        TestPathClassifier.ExtractTestFilesFromArgv(argv, repoRoot, testPatterns);
}
