using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Changes;

public sealed record PullRequestRecord(string Repo, string Url, int Number, string Pushed);

public sealed record ChangeRecord(
    int Version,
    string Branch,
    string Type,
    string Scope,
    string Base,
    List<PullRequestRecord> PullRequests);

/// <summary>Reads, validates, and writes a change's <c>change.jsonc</c> record.</summary>
public sealed class ChangeRecordStore(IFileSystem fileSystem)
{
    public const string RecordFileName = "change.jsonc";

    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public static readonly string[] CommitTypes =
        ["feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore", "revert"];

    private static readonly HashSet<string> AllowedFields = ["version", "branch", "type", "scope", "base", "pullRequests"];
    private static readonly Regex HttpUrl = new(@"^https?://", RegexOptions.Compiled);
    private static readonly Regex CommitSha = new(@"^[0-9a-f]{7,40}\z", RegexOptions.Compiled);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public static List<string> RecordProblems(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } obj)
        {
            return [$"{RecordFileName} must be a JSON object"];
        }

        var problems = new List<string>();
        foreach (var prop in obj.EnumerateObject())
        {
            if (!AllowedFields.Contains(prop.Name))
            {
                problems.Add($"unknown field {prop.Name}");
            }
        }

        if (!obj.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out var verNum) || verNum != 1)
        {
            problems.Add("version must be 1");
        }

        var branch = Jsonc.String(obj, "branch");
        if (string.IsNullOrEmpty(branch))
        {
            problems.Add("branch must be the change branch name");
        }

        var type = Jsonc.String(obj, "type");
        if (type is null || !CommitTypes.Contains(type))
        {
            problems.Add($"type must be one of {string.Join(", ", CommitTypes)}");
        }

        var scope = Jsonc.String(obj, "scope");
        if (string.IsNullOrEmpty(scope))
        {
            problems.Add("scope must be the change's commit scope");
        }

        var baseBranch = Jsonc.String(obj, "base");
        if (string.IsNullOrEmpty(baseBranch))
        {
            problems.Add("base must be the base branch name");
        }

        if (obj.TryGetProperty("pullRequests", out var prs))
        {
            if (prs.ValueKind != JsonValueKind.Array)
            {
                problems.Add("pullRequests must be a list");
            }
            else
            {
                var i = 0;
                foreach (var item in prs.EnumerateArray())
                {
                    var ok = item.ValueKind == JsonValueKind.Object &&
                        Jsonc.String(item, "repo") is not null &&
                        Jsonc.String(item, "url") is { } url && HttpUrl.IsMatch(url) &&
                        item.TryGetProperty("number", out var num) && num.ValueKind == JsonValueKind.Number && num.TryGetInt32(out _) &&
                        Jsonc.String(item, "pushed") is { } pushed && CommitSha.IsMatch(pushed);
                    if (!ok)
                    {
                        problems.Add($"pullRequests[{i}] must have repo, url (http or https), number and pushed (a commit)");
                    }

                    i++;
                }
            }
        }

        return problems;
    }

    public ChangeRecord? Read(string folder)
    {
        var file = Path.Combine(folder, RecordFileName);
        if (!_fs.FileExists(file))
        {
            return null;
        }

        if (!Jsonc.TryParse(_fs.ReadAllText(file), out var raw, out var error) || raw is null)
        {
            throw new CouldNotRunException($"{file} is not valid JSONC: {error ?? "empty file"}");
        }

        if (RecordProblems(raw).Count > 0)
        {
            return null;
        }

        var obj = raw.Value;
        var prs = new List<PullRequestRecord>();
        if (obj.TryGetProperty("pullRequests", out var prArr) && prArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prArr.EnumerateArray())
            {
                prs.Add(new PullRequestRecord(
                    item.GetProperty("repo").GetString()!,
                    item.GetProperty("url").GetString()!,
                    item.GetProperty("number").GetInt32(),
                    item.GetProperty("pushed").GetString()!));
            }
        }

        return new ChangeRecord(
            1,
            obj.GetProperty("branch").GetString()!,
            obj.GetProperty("type").GetString()!,
            obj.GetProperty("scope").GetString()!,
            obj.GetProperty("base").GetString()!,
            prs);
    }

    public static string Serialize(ChangeRecord record)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("branch", record.Branch);
            w.WriteString("type", record.Type);
            w.WriteString("scope", record.Scope);
            w.WriteString("base", record.Base);
            if (record.PullRequests.Count > 0)
            {
                w.WriteStartArray("pullRequests");
                foreach (var pr in record.PullRequests)
                {
                    w.WriteStartObject();
                    w.WriteString("repo", pr.Repo);
                    w.WriteString("url", pr.Url);
                    w.WriteNumber("number", pr.Number);
                    w.WriteString("pushed", pr.Pushed);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n");
        return "// Written by the hoshin program (change start, change rename, change.done); do not edit by hand.\n" + json + "\n";
    }

    public string Write(string folder, ChangeRecord record)
    {
        _fs.CreateDirectory(folder);
        var file = Path.Combine(folder, RecordFileName);
        _fs.WriteAllTextAtomic(file, Serialize(record));
        return file;
    }

    public string BranchOf(string folder)
    {
        var record = Read(folder);
        if (record is not null)
        {
            return record.Branch;
        }

        var older = Path.Combine(folder, "branch.md");
        if (_fs.FileExists(older))
        {
            try
            {
                var branch = Jsonc.String(FrontMatter.Parse(_fs.ReadAllText(older), older).Data, "branch");
                if (!string.IsNullOrEmpty(branch))
                {
                    return branch;
                }
            }
            catch
            {
                // Unreadable branch.md names no branch.
            }
        }

        return $"change/{Path.GetFileName(folder)}";
    }
}
