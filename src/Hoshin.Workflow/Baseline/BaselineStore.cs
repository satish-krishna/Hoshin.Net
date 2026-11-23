using System.Text.Json;
using System.Text.Json.Serialization;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Baseline;

public sealed record BaselineHookRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("ids")] List<string>? Ids,
    [property: JsonPropertyName("output")] string Output);

public sealed record BaselineEntry(
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("hooks")] List<BaselineHookRecord> Hooks);

public sealed record BaselineStoreFile(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("entries")] List<BaselineEntry> Entries);

/// <summary>Stores per-repository baseline results in <c>&lt;git-common-dir&gt;/hoshin/&lt;system&gt;/.baselines/&lt;repo&gt;.json</c>.</summary>
public sealed class BaselineStore(IGitClient git)
{
    private const int KeepCount = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));

    public string BaselineFile(string master, string system, string repo) =>
        _git.GitPath(master, Path.Combine("hoshin", system, ".baselines", $"{repo}.json"), common: true);

    public BaselineEntry? FindEntry(string master, string system, string repo, string baseCommit, string signature) =>
        Read(BaselineFile(master, system, repo), repo)
            .Entries
            .FirstOrDefault(e => e.Base == baseCommit && e.Signature == signature);

    public void SaveEntry(string master, string system, string repo, BaselineEntry entry)
    {
        var file = BaselineFile(master, system, repo);
        var store = Read(file, repo);
        var others = store.Entries.Where(e => !(e.Base == entry.Base && e.Signature == entry.Signature)).ToList();
        others.Add(entry);
        Write(file, store with { Entries = others.TakeLast(KeepCount).ToList() });
    }

    public void DropEntry(string master, string system, string repo, string baseCommit, string signature)
    {
        var file = BaselineFile(master, system, repo);
        if (!File.Exists(file))
        {
            return;
        }

        var store = Read(file, repo);
        Write(file, store with { Entries = store.Entries.Where(e => !(e.Base == baseCommit && e.Signature == signature)).ToList() });
    }

    private static BaselineStoreFile Read(string file, string repo)
    {
        if (!File.Exists(file))
        {
            return new BaselineStoreFile(1, repo, []);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<BaselineStoreFile>(File.ReadAllText(file), JsonOptions);
            return parsed?.Entries is not null ? parsed : new BaselineStoreFile(1, repo, []);
        }
        catch
        {
            return new BaselineStoreFile(1, repo, []);
        }
    }

    private static void Write(string file, BaselineStoreFile store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = $"{file}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(store, JsonOptions) + "\n");
        File.Move(temp, file, overwrite: true);
    }
}
