using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

/// <summary>Commit scope resolution and branch commit-rule verification.</summary>
public sealed class ScopeRules(ChangeRecordStore changeRecords, MarkerReader markerReader, IFileSystem fileSystem)
{
    private static readonly Regex HeaderRegex = new(@"^[a-z]+\(([^)\s]+)\)!?: \S", RegexOptions.Compiled);

    private readonly ChangeRecordStore _changeRecords = changeRecords ?? throw new ArgumentNullException(nameof(changeRecords));
    private readonly MarkerReader _markerReader = markerReader ?? throw new ArgumentNullException(nameof(markerReader));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public string ChangeScope(string master, string system, string folder, IReadOnlyList<string>? markers = null)
    {
        var intent = Path.Combine(folder, "intent.md");
        if (_fs.FileExists(intent))
        {
            try
            {
                var scope = Jsonc.String(FrontMatter.Parse(_fs.ReadAllText(intent), intent).Data, "scope");
                if (!string.IsNullOrEmpty(scope) && NamingService.CheckScope(scope).Count == 0)
                {
                    return scope;
                }
            }
            catch
            {
                // Fall through to change.jsonc / system scope.
            }
        }

        var recorded = _changeRecords.Read(folder)?.Scope;
        if (!string.IsNullOrEmpty(recorded) && NamingService.CheckScope(recorded).Count == 0)
        {
            return recorded;
        }

        return SystemScope(master, system, markers);
    }

    public string SystemScope(string master, string system, IReadOnlyList<string>? markers = null)
    {
        try
        {
            var markerNames = markers ?? MarkerConstants.DefaultMarkers;
            var file = _markerReader.MarkerFileIn(master, markerNames) ?? Path.Combine(master, markerNames[0]);
            using var doc = JsonDocument.Parse(_fs.ReadAllText(file));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("systems", out var systems) &&
                systems.ValueKind == JsonValueKind.Object &&
                systems.TryGetProperty(system, out var sys) &&
                sys.ValueKind == JsonValueKind.Object &&
                sys.TryGetProperty("commitScope", out var scopeProp) &&
                scopeProp.ValueKind == JsonValueKind.String &&
                MarkerReader.CommitScopeRegex.IsMatch(scopeProp.GetString()!))
            {
                return scopeProp.GetString()!;
            }
        }
        catch
        {
            // Unreadable marker has no custom commitScope.
        }

        return system;
    }

    public static List<string> CommitRuleProblems(IEnumerable<GitCommitInfo> commits, string scope)
    {
        var problems = new List<string>();
        foreach (var c in commits)
        {
            var why = new List<string>();
            var m = HeaderRegex.Match(c.Subject);
            if (!m.Success || m.Groups[1].Value != scope)
            {
                why.Add($"its header does not carry the scope {scope}");
            }

            if (!c.GeneratedByHoshin)
            {
                why.Add("it has no Generated-By: hoshin trailer");
            }

            if (why.Count > 0)
            {
                var shortSha = c.Sha.Length >= 7 ? c.Sha[..7] : c.Sha;
                problems.Add($"{shortSha} {c.Subject}: {string.Join(", and ", why)}");
            }
        }

        return problems;
    }
}
