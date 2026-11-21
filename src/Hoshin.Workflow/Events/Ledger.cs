using System.Text.Json;
using System.Text.Json.Serialization;
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
    [property: JsonPropertyName("reds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<RedRecord>? Reds = null);

/// <summary>Atomic per-repository ledger storage in <c>&lt;git-common-dir&gt;/hoshin/&lt;system&gt;/&lt;change&gt;/&lt;repo&gt;.json</c>.</summary>
public sealed class LedgerStore(IGitClient? git = null)
{
    public static LedgerStore Default { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;

    public string LedgerFolder(string master, string system, string change) =>
        _git.GitPath(master, Path.Combine("hoshin", system, change), common: true);

    public RepoLedger? Read(string folder, string repo)
    {
        var file = Path.Combine(folder, $"{repo}.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RepoLedger>(File.ReadAllText(file), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public List<RepoLedger> ReadAll(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var list = new List<RepoLedger>();
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
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
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{ledger.Repo}.json");
        var temp = $"{file}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(ledger, JsonOptions) + "\n");
        File.Move(temp, file, overwrite: true);
    }

    public static RepoLedger Empty(string system, string change, string repo, string path) =>
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
