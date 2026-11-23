using System.Text;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using YamlDotNet.Core;

namespace Hoshin.Workflow.Migration;

internal sealed record MigrationTransformResult(List<string> Lines, bool Changed);

/// <summary>
/// Rewrites Layout 1 and Layout 2 YAML Markdown documents (<c>intent.md</c>, <c>spec.md</c>, <c>plan.md</c>,
/// <c>build/*.md</c>, <c>build/review.md</c>, <c>context/*.md</c>, <c>branch.md</c>) and <c>change.yml</c> into
/// canonical Layout 2 JSONC and <c>change.jsonc</c>.
/// </summary>
public sealed class DocumentMigrator(IYamlToJsoncConverter yamlConverter) : IDocumentMigrator
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs",
    };

    private readonly IYamlToJsoncConverter _yaml = yamlConverter ?? throw new ArgumentNullException(nameof(yamlConverter));

    private enum MigrationKind
    {
        FrontMatterOnly,
        Spec,
        Plan,
        BuildLog,
        Review,
    }

    public string MigrateChangeYamlText(string yamlText) =>
        _yaml.ConvertChangeYaml(yamlText) + "\n";

    public string MigrateMarkdownText(string text, string filePath, out bool changed)
    {
        var kind = ClassifyMarkdown(filePath);
        var normalized = Doc.Normalize(text);
        var lines = normalized.Split('\n');
        var trailingNewline = normalized.EndsWith('\n');

        var result = kind switch
        {
            MigrationKind.Spec => MigrateSpec(lines),
            MigrationKind.Plan => MigratePlan(lines),
            MigrationKind.BuildLog => MigrateBuildLog(lines),
            MigrationKind.Review => MigrateReview(lines),
            _ => MigrateFrontMatter(lines, forceLayout2: false, insertIfMissing: false),
        };

        if (!result.Changed)
        {
            changed = false;
            return text;
        }

        var output = string.Join('\n', result.Lines);
        if (trailingNewline && !output.EndsWith('\n'))
        {
            output += "\n";
        }

        if (output == normalized)
        {
            changed = false;
            return text;
        }

        changed = true;
        return output;
    }

    public DocumentMigrationResult MigratePaths(IReadOnlyList<string> paths, bool write)
    {
        var targets = DiscoverTargets(paths);
        var items = new List<DocumentMigrationItem>();
        var problems = new List<string>();

        foreach (var file in targets)
        {
            var item = EvaluateOrMigrateFile(file, write);
            items.Add(item);
            if (item.Status == "refused")
            {
                problems.Add($"{item.Path}: {item.Detail}");
            }
        }

        return new DocumentMigrationResult(write, items, problems);
    }

    public List<DocumentMigrationItem> PlanDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return DiscoverTargets([directory])
            .Select(file => EvaluateOrMigrateFile(file, write: false))
            .Where(item => item.Status != "current")
            .ToList();
    }

    private DocumentMigrationItem EvaluateOrMigrateFile(string file, bool write)
    {
        var name = Path.GetFileName(file);
        if (name.Equals("change.yml", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateOrMigrateChangeYaml(file, write);
        }

        var kindLabel = KindName(ClassifyMarkdown(file));
        try
        {
            var original = File.ReadAllText(file);
            var migrated = MigrateMarkdownText(original, file, out var changed);
            if (!changed)
            {
                return new DocumentMigrationItem(file, null, kindLabel, "current", null);
            }

            if (!write)
            {
                return new DocumentMigrationItem(file, null, kindLabel, "would", null);
            }

            File.WriteAllText(file, migrated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new DocumentMigrationItem(file, null, kindLabel, "done", null);
        }
        catch (FormatException e)
        {
            return new DocumentMigrationItem(file, null, kindLabel, "refused", e.Message);
        }
        catch (YamlException e)
        {
            return new DocumentMigrationItem(file, null, kindLabel, "refused", e.Message);
        }
        catch (IOException e)
        {
            return new DocumentMigrationItem(file, null, kindLabel, "refused", e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            return new DocumentMigrationItem(file, null, kindLabel, "refused", e.Message);
        }
    }

    private DocumentMigrationItem EvaluateOrMigrateChangeYaml(string file, bool write)
    {
        var dir = Path.GetDirectoryName(file)!;
        var target = Path.Combine(dir, ChangeRecordStore.RecordFileName);
        if (File.Exists(target))
        {
            return new DocumentMigrationItem(file, target, "change", "refused", $"both change.yml and {ChangeRecordStore.RecordFileName} exist");
        }

        string converted;
        try
        {
            var sourceYaml = File.ReadAllText(file);
            converted = MigrateChangeYamlText(sourceYaml);
        }
        catch (FormatException e)
        {
            return new DocumentMigrationItem(file, target, "change", "refused", e.Message);
        }
        catch (YamlException e)
        {
            return new DocumentMigrationItem(file, target, "change", "refused", e.Message);
        }
        catch (IOException e)
        {
            return new DocumentMigrationItem(file, target, "change", "refused", e.Message);
        }

        if (!write)
        {
            return new DocumentMigrationItem(file, target, "change", "would", null);
        }

        try
        {
            File.WriteAllText(target, converted, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Delete(file);
            return new DocumentMigrationItem(file, target, "change", "done", null);
        }
        catch (IOException e)
        {
            return new DocumentMigrationItem(file, target, "change", "refused", e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            return new DocumentMigrationItem(file, target, "change", "refused", e.Message);
        }
    }

    private MigrationTransformResult MigrateSpec(string[] lines)
    {
        var fm = MigrateFrontMatter(lines, forceLayout2: true, insertIfMissing: true);
        return SpecAndReviewMigrator.UpgradeSpecBody(fm.Lines, fm.Changed);
    }

    private MigrationTransformResult MigratePlan(string[] lines)
    {
        var fm = MigrateFrontMatter(lines, forceLayout2: true, insertIfMissing: true);
        return PlanDocumentMigrator.UpgradePlanBody(fm.Lines, fm.Changed, RewriteYamlFences);
    }

    private MigrationTransformResult MigrateBuildLog(string[] lines)
    {
        var fm = MigrateFrontMatter(lines, forceLayout2: true, insertIfMissing: true);
        return BuildLogMigrator.UpgradeBuildLogBody(fm.Lines, fm.Changed, RewriteYamlFences);
    }

    private MigrationTransformResult MigrateReview(string[] lines)
    {
        var fm = MigrateFrontMatter(lines, forceLayout2: true, insertIfMissing: true);
        var cur = fm.Lines;
        var changed = fm.Changed;

        var doc = Doc.Split(string.Join('\n', cur));
        var yamlRewrites = SpecAndReviewMigrator.FindReviewYamlRecordFences(doc);
        if (yamlRewrites.Count > 0)
        {
            cur = RewriteYamlFences(doc, yamlRewrites);
            changed = true;
        }

        return new MigrationTransformResult(cur, changed);
    }

    private MigrationTransformResult MigrateFrontMatter(string[] lines, bool forceLayout2, bool insertIfMissing) =>
        FrontMatterMigrator.Migrate(_yaml, lines, forceLayout2, insertIfMissing);

    internal static bool IsYamlFence(Fence f) =>
        f.Info.Equals("yaml", StringComparison.OrdinalIgnoreCase) ||
        f.Info.Equals("yml", StringComparison.OrdinalIgnoreCase);

    internal static Fence? FindYamlBlock(Doc doc, int from, int limit) =>
        doc.Fences.FirstOrDefault(f => IsYamlFence(f) && f.Open >= from && f.Open < limit);

    private List<string> RewriteYamlFences(Doc doc, List<(Fence Fence, bool ExpectArray)> targets)
    {
        var replacements = new List<(int Start, int EndExclusive, List<string> Replacement)>();
        foreach (var (fence, expectArray) in targets.OrderBy(t => t.Fence.Open))
        {
            var close = fence.Close < 0 ? doc.Lines.Length : fence.Close;
            var openLine = doc.Lines[fence.Open];
            var indent = openLine[..(openLine.Length - openLine.TrimStart().Length)];
            var fenceChars = new string(fence.Char, fence.Length);
            var yamlText = string.Join('\n', doc.Lines[(fence.Open + 1)..close]);
            var jsonc = _yaml.ConvertBlock(yamlText, new YamlConversionOptions(YamlToJsoncConverter.DefaultStringKeys, ForceLayout2: false, ExpectArray: expectArray));

            var block = new List<string> { $"{indent}{fenceChars}jsonc" };
            foreach (var line in jsonc.Split('\n'))
            {
                block.Add(indent + line);
            }

            if (fence.Close >= 0)
            {
                block.Add(doc.Lines[fence.Close]);
                replacements.Add((fence.Open, fence.Close + 1, block));
            }
            else
            {
                replacements.Add((fence.Open, doc.Lines.Length, block));
            }
        }

        return ApplyReplacements(doc.Lines, replacements);
    }

    internal static List<string> ApplyReplacements(string[] lines, List<(int Start, int EndExclusive, List<string> Replacement)> replacements)
    {
        var sorted = replacements.OrderBy(r => r.Start).ToList();
        var result = new List<string>(lines.Length);
        var cursor = 0;

        foreach (var (start, endExclusive, replacement) in sorted)
        {
            if (start < cursor)
            {
                continue;
            }

            for (var i = cursor; i < start; i++)
            {
                result.Add(lines[i]);
            }

            result.AddRange(replacement);
            cursor = endExclusive;
        }

        for (var i = cursor; i < lines.Length; i++)
        {
            result.Add(lines[i]);
        }

        return result;
    }

    private static List<string> DiscoverTargets(IReadOnlyList<string> paths)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void AddFile(string path)
        {
            var full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                found.Add(full);
            }
        }

        void Walk(string dir)
        {
            foreach (var file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("change.yml", StringComparison.OrdinalIgnoreCase))
                {
                    AddFile(file);
                }
            }

            foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(sub)))
                {
                    Walk(sub);
                }
            }
        }

        foreach (var raw in paths)
        {
            if (File.Exists(raw))
            {
                AddFile(raw);
            }
            else if (Directory.Exists(raw))
            {
                Walk(raw);
            }
            else
            {
                throw new CouldNotRunException($"{raw}: cannot read path: ENOENT");
            }
        }

        return found;
    }

    private static MigrationKind ClassifyMarkdown(string filePath)
    {
        var parts = filePath.Split(['/', '\\']);
        var name = parts.Length > 0 ? parts[^1] : "";
        var parent = parts.Length > 1 ? parts[^2] : "";

        if (name.Equals("spec.md", StringComparison.OrdinalIgnoreCase))
        {
            return MigrationKind.Spec;
        }

        if (name.Equals("plan.md", StringComparison.OrdinalIgnoreCase))
        {
            return MigrationKind.Plan;
        }

        if (parent.Equals("build", StringComparison.OrdinalIgnoreCase) && name.Equals("review.md", StringComparison.OrdinalIgnoreCase))
        {
            return MigrationKind.Review;
        }

        if (parent.Equals("build", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return MigrationKind.BuildLog;
        }

        return MigrationKind.FrontMatterOnly;
    }

    private static string KindName(MigrationKind kind) => kind switch
    {
        MigrationKind.Spec => "spec",
        MigrationKind.Plan => "plan",
        MigrationKind.BuildLog => "build-log",
        MigrationKind.Review => "review",
        _ => "document",
    };
}
