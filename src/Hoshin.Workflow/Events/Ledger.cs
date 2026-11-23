using System.Text.Json;
using System.Text.Json.Serialization;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Events;

public sealed record HookRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("when")] string When,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("ids")] List<string>? Ids = null);

public sealed record FailedHookSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("onFailure")] string OnFailure);

public sealed record EventRecord(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("hooks")] List<HookRecord> Hooks,
    [property: JsonPropertyName("problems")] List<string> Problems,
    [property: JsonPropertyName("failed")] FailedHookSummary? Failed,
    [property: JsonPropertyName("details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, object?>? Details = null);

public sealed record BaselineApproval(
    [property: JsonPropertyName("hook")] string Hook,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("at")] string At);

public sealed record RedRecord(
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("compile")] string[]? Compile,
    [property: JsonPropertyName("test")] string[] Test,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("at")] string At);

public sealed record RepoLedger(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("change")] string Change,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("runs")] List<EventRecord> Runs,
    [property: JsonPropertyName("gateBlocks")] Dictionary<string, int> GateBlocks,
    [property: JsonPropertyName("approvals"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<BaselineApproval>? Approvals = null,
    [property: JsonPropertyName("reds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<RedRecord>? Reds = null,
    [property: JsonPropertyName("issued"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WorkflowIssued>? Issued = null,
    [property: JsonPropertyName("verdicts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WorkflowVerdict>? Verdicts = null,
    [property: JsonPropertyName("baselines"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WorkflowBaselineRun>? Baselines = null,
    [property: JsonPropertyName("checks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WorkflowCheckRun>? Checks = null,
    [property: JsonPropertyName("reviews"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WorkflowReviewRun>? Reviews = null,
    [property: JsonPropertyName("workflowApprovals"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WorkflowApproval>? WorkflowApprovals = null,
    [property: JsonPropertyName("published"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WorkflowPublished>? Published = null,
    [property: JsonPropertyName("activeSubagent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActiveSubagent? ActiveSubagent = null)
{
    [JsonIgnore]
    public IReadOnlyList<WorkflowIssued> WorkflowIssued => Issued ?? [];

    [JsonIgnore]
    public IReadOnlyList<WorkflowVerdict> WorkflowVerdicts => Verdicts ?? [];

    [JsonIgnore]
    public IReadOnlyList<WorkflowBaselineRun> WorkflowBaselines => Baselines ?? [];

    [JsonIgnore]
    public IReadOnlyList<WorkflowCheckRun> WorkflowChecks => Checks ?? [];

    [JsonIgnore]
    public IReadOnlyList<WorkflowReviewRun> WorkflowReviews => Reviews ?? [];

    [JsonIgnore]
    public IReadOnlyList<WorkflowApproval> WorkflowApprovalRecords => WorkflowApprovals ?? [];

    [JsonIgnore]
    public IReadOnlyList<WorkflowPublished> WorkflowPublishedRecords => Published ?? [];
}

/// <summary>Atomic per-repository ledger storage in <c>&lt;git-common-dir&gt;/hoshin/&lt;system&gt;/&lt;change&gt;/&lt;repo&gt;.json</c>.</summary>
public sealed class LedgerStore(IGitWorktrees git, IFileSystem fileSystem)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IGitWorktrees _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public string LedgerFolder(string master, string system, string change) =>
        _git.GitPath(master, Path.Combine("hoshin", system, change), common: true);

    public RepoLedger? Read(string folder, string repo)
    {
        var file = Path.Combine(folder, $"{repo}.json");
        if (!_fs.FileExists(file))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RepoLedger>(_fs.ReadAllText(file), JsonOptions);
            return parsed ?? throw new CouldNotRunException($"{file} is not valid JSON: file contains null");
        }
        catch (JsonException e)
        {
            throw new CouldNotRunException($"{file} is not valid JSON: {e.Message.Split('\n')[0]}");
        }
    }

    public List<RepoLedger> ReadAll(string folder)
    {
        if (!_fs.DirectoryExists(folder))
        {
            return [];
        }

        var list = new List<RepoLedger>();
        foreach (var file in _fs.EnumerateFiles(folder, "*.json").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            var repo = Path.GetFileNameWithoutExtension(file);
            var ledger = Read(folder, repo);
            if (ledger is not null)
            {
                list.Add(ledger);
            }
        }

        return list;
    }

    public void Write(string folder, RepoLedger ledger)
    {
        _fs.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{ledger.Repo}.json");
        _fs.WriteAllTextAtomic(file, JsonSerializer.Serialize(ledger, JsonOptions) + "\n");
    }

    public RepoLedger Update(string folder, string repo, Func<RepoLedger, RepoLedger> transform) =>
        Update(
            folder,
            repo,
            () => throw new CouldNotRunException($"{Path.Combine(folder, $"{repo}.json")} does not exist"),
            transform);

    public RepoLedger Update(string folder, string repo, Func<RepoLedger> createDefault, Func<RepoLedger, RepoLedger> transform)
    {
        _fs.CreateDirectory(folder);
        using var gate = FileLock.Acquire(Path.Combine(folder, $"{repo}.lock"), TimeSpan.FromSeconds(10));
        var current = Read(folder, repo) ?? createDefault();
        var updated = transform(current);
        Write(folder, updated);
        return updated;
    }

    public static RepoLedger Empty(string system = "default", string change = "default", string repo = "master", string path = "") =>
        new(1, system, change, repo, path, [], new Dictionary<string, int>(StringComparer.Ordinal));

    public static EventRecord? Latest(RepoLedger? ledger, string eventName, string? key, string? subject = null)
    {
        if (ledger is null)
        {
            return null;
        }

        for (var i = ledger.Runs.Count - 1; i >= 0; i--)
        {
            var r = ledger.Runs[i];
            if (r.Event == eventName && (key is null || r.Key == key) && (subject is null || r.Subject == subject))
            {
                return r;
            }
        }

        return null;
    }

    public static RepoLedger AppendRun(RepoLedger ledger, EventRecord record)
    {
        var runs = ledger.Runs.Append(record).TakeLast(200).ToList();
        return ledger with { Runs = runs };
    }
}
