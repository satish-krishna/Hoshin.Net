using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Hoshin.Workflow.Events;

public sealed record CheckReadItem(
    string Check,
    string State,
    string StartedAt,
    string? EndedAt,
    double? Timeout,
    string? Outcome,
    List<string>? Ids,
    bool? Blocks);

public sealed record CheckRunRead(string Run, string Event, string Repo, string Phase, List<CheckReadItem> Checks);

public sealed record ReadChecksResult(List<CheckRunRead> Runs, List<string> Problems);

/// <summary>Reads <c>checks.jsonl</c> into runs and determines each check's state (<c>ended</c>, <c>running</c>, <c>abandoned</c>).</summary>
public static class CheckReader
{
    public static bool IsPidAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // EPERM / access denied means the process exists.
            return true;
        }
    }

    private sealed class OpenCheck
    {
        public required string Check { get; init; }
        public string State { get; set; } = "running";
        public required string StartedAt { get; init; }
        public string? EndedAt { get; set; }
        public double? Timeout { get; init; }
        public string? Outcome { get; set; }
        public List<string>? Ids { get; set; }
        public bool? Blocks { get; set; }
        public bool HasPid { get; init; }
        public long? Pid { get; init; }
        public DateTimeOffset? At { get; init; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public static ReadChecksResult Read(string file, string? repoFilter = null, DateTimeOffset? now = null, Func<int, bool>? pidAlive = null)
    {
        var currentTime = now ?? TimeProvider.System.GetUtcNow();
        var alive = pidAlive ?? IsPidAlive;
        var problems = new List<string>();
        if (!File.Exists(file))
        {
            return new ReadChecksResult([], problems);
        }

        var text = File.ReadAllText(file);
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        var lastNl = text.LastIndexOf('\n');
        if (lastNl < 0)
        {
            return new ReadChecksResult([], problems);
        }

        var complete = text[..lastNl];
        var lines = complete.Split('\n');

        var runs = new Dictionary<string, (CheckRunRead Run, List<OpenCheck> Opens)>(StringComparer.Ordinal);
        var runOrder = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch
            {
                // Reported below.
            }

            using (doc)
            {
                if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                {
                    problems.Add($"line {i + 1} of {file} is not a check event");
                    continue;
                }

                var root = doc.RootElement;
                var type = typeProp.GetString()!;
                if (type is not ("check-start" or "check-end"))
                {
                    continue;
                }

                var runId = GetStr(root, "run");
                var check = GetStr(root, "check");
                var at = GetStr(root, "at");
                var repo = GetStr(root, "repo");
                var phase = GetStr(root, "phase");
                var ev = GetStr(root, "event");

                double? timeout = null;
                var validTimeout = true;
                if (type == "check-start")
                {
                    if (root.TryGetProperty("timeout", out var tp) && tp.ValueKind == JsonValueKind.Number && tp.TryGetDouble(out var tv) && double.IsFinite(tv))
                    {
                        timeout = tv;
                    }
                    else
                    {
                        validTimeout = false;
                    }
                }

                if (runId is null || check is null || at is null || repo is null || phase is null || ev is null || !validTimeout)
                {
                    problems.Add($"line {i + 1} of {file} is not a check event");
                    continue;
                }

                if (type == "check-start")
                {
                    if (!runs.TryGetValue(runId, out var entry))
                    {
                        entry = (new CheckRunRead(runId, ev, repo, phase, []), []);
                        runs[runId] = entry;
                        runOrder.Add(runId);
                    }

                    var hasPid = root.TryGetProperty("pid", out var pidProp);
                    long? pid = null;
                    if (hasPid && pidProp.ValueKind == JsonValueKind.Number && pidProp.TryGetInt64(out var pv))
                    {
                        pid = pv;
                    }

                    DateTimeOffset? parsedAt = DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
                        ? dto
                        : null;

                    var open = new OpenCheck
                    {
                        Check = check,
                        StartedAt = at,
                        Timeout = timeout,
                        HasPid = hasPid,
                        Pid = pid,
                        At = parsedAt,
                    };
                    entry.Opens.Add(open);
                }
                else
                {
                    if (!runs.TryGetValue(runId, out var entry))
                    {
                        continue;
                    }

                    var open = entry.Opens.FirstOrDefault(c => c.Check == check && c.State == "running");
                    if (open is null)
                    {
                        continue;
                    }

                    open.State = "ended";
                    open.EndedAt = at;
                    open.Outcome = GetStr(root, "outcome");
                    if (root.TryGetProperty("ids", out var idsProp) && idsProp.ValueKind == JsonValueKind.Array)
                    {
                        open.Ids = idsProp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
                    }

                    if (root.TryGetProperty("blocks", out var blocksProp) && blocksProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        open.Blocks = blocksProp.GetBoolean();
                    }
                }
            }
        }

        var ordered = runOrder.Select(id => runs[id]).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var (run, opens) = ordered[index];
            var superseded = ordered.Skip(index + 1).Any(later => later.Run.Repo == run.Repo && later.Run.Phase == run.Phase);
            foreach (var c in opens)
            {
                if (c.State != "running")
                {
                    continue;
                }

                var expired = c.Timeout.HasValue && c.At.HasValue && c.At.Value.AddSeconds(c.Timeout.Value) <= currentTime;
                var gone = c.HasPid && !(c.Pid.HasValue && c.Pid.Value > 0 && c.Pid.Value <= int.MaxValue && alive((int)c.Pid.Value));
                if (superseded || expired || gone)
                {
                    c.State = "abandoned";
                }
            }
        }

        var kept = ordered
            .Where(e => repoFilter is null || e.Run.Repo == repoFilter)
            .Select(e => e.Run with
            {
                Checks = e.Opens.Select(c => new CheckReadItem(c.Check, c.State, c.StartedAt, c.EndedAt, c.Timeout, c.Outcome, c.Ids, c.Blocks)).ToList(),
            })
            .ToList();

        return new ReadChecksResult(kept, problems);
    }

    private static string? GetStr(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
