using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

/// <summary>Checks documents, cross-document references, <c>.hoshin.json</c>, <c>hooks.json</c>, and <c>change.jsonc</c>.</summary>
public sealed class DocumentCheckService(IDocumentReader? reader = null, IGitClient? git = null)
{
    public static DocumentCheckService Default { get; } = new();

    private readonly IDocumentReader _reader = reader ?? new DocumentReader();
    private readonly IGitClient _git = git ?? ProcessGitClient.Default;

    public List<Problem> CheckFiles(IReadOnlyList<string> files)
    {
        var problems = new List<Problem>();
        var docs = new List<ChangeDoc>();

        foreach (var file in files)
        {
            if (IsConfigFile(file))
            {
                problems.AddRange(CheckConfigFile(file));
            }
            else
            {
                var marker = MarkerReader.FindMarker(file);
                var kind = _reader.KindOf(file);
                IReadOnlyList<string>? allowed = null;
                string? system = null;
                if (kind == DocKind.Plan)
                {
                    if (!File.Exists(file))
                    {
                        throw new CouldNotRunException($"{file}: cannot read the file: ENOENT");
                    }

                    var fm = FrontMatter.Parse(File.ReadAllText(file), file);
                    system = Jsonc.String(fm.Data, "system") ?? "";
                    allowed = MarkerReader.AllowedPlanRepos(marker, system);
                }

                var result = _reader.ReadFile(file, allowed, system);
                problems.AddRange(result.Problems);
                docs.Add(new ChangeDoc(file, result));
            }
        }

        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var groups = new Dictionary<string, List<ChangeDoc>>(cmp);
        foreach (var d in docs)
        {
            var key = ChangeFolderOf(d.File);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(d);
        }

        foreach (var group in groups.Values)
        {
            problems.AddRange(CrossChecker.CheckChange(group));
        }

        return ProblemFormatter.Sort(problems);
    }

    private static string ChangeFolderOf(string file)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return Path.GetFileName(folder) == "build" ? Path.GetDirectoryName(folder)! : folder;
    }

    private static bool IsConfigFile(string file)
    {
        var name = Path.GetFileName(file);
        if (name == MarkerConstants.FileName || name == ChangeRecordStore.RecordFileName)
        {
            return true;
        }

        if (name == "hooks.json")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            var grandParent = dir is null ? null : Path.GetDirectoryName(dir);
            return grandParent is not null && Path.GetFileName(grandParent) == "intent";
        }

        return false;
    }

    private List<Problem> CheckConfigFile(string file)
    {
        if (!File.Exists(file))
        {
            throw new CouldNotRunException($"{file}: cannot read the file: ENOENT");
        }

        var name = Path.GetFileName(file);
        var text = File.ReadAllText(file);

        if (name == ChangeRecordStore.RecordFileName)
        {
            if (!Jsonc.TryParse(text, out var raw, out var err))
            {
                return [new Problem(file, 1, $"{ChangeRecordStore.RecordFileName} is not JSONC: {err}")];
            }

            return ChangeRecordStore.RecordProblems(raw).Select(m => new Problem(file, 1, m)).ToList();
        }

        if (name == MarkerConstants.FileName)
        {
            var hookProblems = HookConfigService.ParseConfig(text, null, "").Problems
                .Select(p => p.StartsWith(".hoshin.json: ", StringComparison.Ordinal) ? p[".hoshin.json: ".Length..] : p);
            var layoutProblems = LayoutProblems(file, text);
            return hookProblems.Concat(layoutProblems).Select(m => new Problem(file, 1, m)).ToList();
        }

        var full = Path.GetFullPath(file);
        var systemDir = Path.GetDirectoryName(full)!;
        var system = Path.GetFileName(systemDir);
        var intentDir = Path.GetDirectoryName(systemDir)!;
        var repoRoot = Path.GetDirectoryName(intentDir)!;
        var markerPath = Path.Combine(repoRoot, MarkerConstants.FileName);
        var markerText = File.Exists(markerPath) ? File.ReadAllText(markerPath) : null;
        var prefix = $"intent/{system}/hooks.json: ";

        return HookConfigService.ParseConfig(markerText, text, system).Problems
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => new Problem(file, 1, p[prefix.Length..]))
            .ToList();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private List<string> LayoutProblems(string file, string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch
        {
            return [];
        }

        using (doc)
        {
            var raw = doc.RootElement;
            var problems = MarkerReader.MarkerProblems(raw);
            if (!raw.TryGetProperty("layout", out var lp) || lp.ValueKind != JsonValueKind.String || lp.GetString() != "single-repo" ||
                !raw.TryGetProperty("systems", out var systems) || systems.ValueKind != JsonValueKind.Object)
            {
                return problems;
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            var top = _git.TopLevel(dir);
            var baseBranch = top is null ? null : _git.BaseBranch(top);
            var revision = top is null || baseBranch is null ? null : _git.BaseRevision(top, baseBranch);
            if (top is null || revision is null)
            {
                return problems;
            }

            foreach (var sys in systems.EnumerateObject())
            {
                if (sys.Value.ValueKind != JsonValueKind.Object || !sys.Value.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var app in apps.EnumerateObject())
                {
                    var normal = MarkerReader.NormalizeFolder(app.Value);
                    if (normal is not null && normal != "." && _git.ListFiles(top, revision, normal).Count == 0)
                    {
                        problems.Add($"app {app.Name}: the folder {normal} does not exist on the base branch {baseBranch}");
                    }
                }
            }

            return problems;
        }
    }
}
