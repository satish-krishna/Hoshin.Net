using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Migration;

/// <summary>
/// Rewrites Layout 1 and Layout 2 YAML Markdown documents (<c>intent.md</c>, <c>spec.md</c>, <c>plan.md</c>,
/// <c>build/*.md</c>, <c>build/review.md</c>, <c>context/*.md</c>, <c>branch.md</c>) and <c>change.yml</c> into
/// canonical Layout 2 JSONC and <c>change.jsonc</c>.
/// </summary>
public sealed class DocumentMigrator(IYamlToJsoncConverter? yamlConverter = null) : IDocumentMigrator
{
    public static DocumentMigrator Default { get; } = new();

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs",
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Spec Layout 1 patterns (matching tools/documents/spec.ts).
    private const string Sep = @"[-\u2013\u2014]";
    private static readonly Regex SpecRuleWithFrom = new($@"^(\s*)Rule:\s*(R\d+)\s*{Sep}\s*(.*?)\s*\(from:\s*(.*)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex SpecRulePlain = new($@"^(\s*)Rule:\s*(R\d+)\s*{Sep}\s*(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex SpecScenario = new($@"^(\s*)Scenario(\s+Outline)?:\s*(R\d+\.\d+)\s*{Sep}\s*(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex SpecHasLayout2Tag = new(@"^\s*@R\d+", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // Plan patterns (matching tools/documents/plan.ts).
    private static readonly Regex PlanHeading1 = new(@"^### (T\d+): (.*?)\s+\(`([\p{L}\p{N}_.-]+)`\)\s*$", RegexOptions.Compiled);
    private static readonly Regex PlanHeading2 = new(@"^### (T\d+):\s*(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex PlanFieldBullet = new(@"^- \*\*(.+?):\*\*\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex PlanAnyHeading = new(@"^#{1,3} ", RegexOptions.Compiled);
    private static readonly Regex PlanTopHeading = new(@"^#{1,2} ", RegexOptions.Compiled);
    private static readonly Regex PlanSectionHeading = new(@"^#### ", RegexOptions.Compiled);
    private static readonly Regex PlanSharedHeading = new(@"^## Shared decisions\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlanCoverageHeading = new(@"^## Scenario coverage\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TaskIdToken = new(@"^T\d+$", RegexOptions.Compiled);
    private static readonly Regex ScenarioIdRegex = new(@"^R\d+(?:\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNum = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorCell = new(@"^:?-+:?$", RegexOptions.Compiled);

    // Build log patterns (matching tools/documents/build-log.ts).
    private static readonly Regex BuildEntryHeading = new(@"^### (T\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex BuildTableSeparatorCell = new(@"^[-: ]+$", RegexOptions.Compiled);

    // Review patterns (matching tools/documents/review.ts).
    private static readonly Regex ReviewHeading = new(@"^## Review (\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex FindingHeading = new(@"^### (F\d+)\s*$", RegexOptions.Compiled);

    private readonly IYamlToJsoncConverter _yaml = yamlConverter ?? YamlToJsoncConverter.Default;

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
            _ => MigrateFrontMatterOnly(lines),
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

    internal DocumentMigrationItem ExecuteFile(string file) => EvaluateOrMigrateFile(file, write: true);

    private DocumentMigrationItem EvaluateOrMigrateFile(string file, bool write)
    {
        var name = Path.GetFileName(file);
        if (name.Equals("change.yml", StringComparison.OrdinalIgnoreCase))
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
                converted = MigrateChangeYamlText(File.ReadAllText(file));
                if (!Jsonc.TryParse(converted, out _, out var err))
                {
                    return new DocumentMigrationItem(file, target, "change", "refused", $"migrated change.jsonc is not valid JSONC: {err}");
                }
            }
            catch (Exception e)
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
            catch (Exception e)
            {
                return new DocumentMigrationItem(file, target, "change", "refused", e.Message);
            }
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
        catch (Exception e)
        {
            return new DocumentMigrationItem(file, null, kindLabel, "refused", e.Message);
        }
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
                var name = Path.GetFileName(sub);
                if (!SkippedDirectories.Contains(name))
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

    private sealed record TransformResult(List<string> Lines, bool Changed);

    // -------------------------------------------------------------------------
    // Front matter migration
    // -------------------------------------------------------------------------

    private TransformResult MigrateFrontMatterOnly(string[] lines) =>
        MigrateFrontMatter(lines, forceLayout2: false, insertIfMissing: false);

    private TransformResult MigrateFrontMatter(string[] lines, bool forceLayout2, bool insertIfMissing)
    {
        // Case 1: YAML front matter delimited by --- on line 1.
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var close = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            if (close < 0)
            {
                throw new FormatException("YAML front matter is never closed");
            }

            var yamlBody = string.Join('\n', lines[1..close]);
            var jsonc = _yaml.ConvertFrontMatter(yamlBody, forceLayout2);
            var outLines = new List<string> { "```jsonc" };
            outLines.AddRange(jsonc.Split('\n'));
            outLines.Add("```");
            outLines.AddRange(lines[(close + 1)..]);
            return new TransformResult(outLines, Changed: true);
        }

        // Case 2: Already JSONC front matter on line 1.
        var doc = Doc.Split(string.Join('\n', lines));
        var fence = doc.Fences.FirstOrDefault(f => f.Open == 0 && Doc.IsFieldsBlock(f));
        if (fence is not null && fence.Close > 0)
        {
            if (!forceLayout2)
            {
                return new TransformResult([.. lines], Changed: false);
            }

            var block = doc.BlockOf(fence);
            if (Jsonc.TryParse(block.Text, out var parsed, out _) &&
                parsed is { ValueKind: JsonValueKind.Object } obj &&
                obj.TryGetProperty("layout", out var lp) &&
                lp.ValueKind == JsonValueKind.Number &&
                lp.TryGetInt32(out var ver) && ver >= 2)
            {
                return new TransformResult([.. lines], Changed: false);
            }

            var updated = UpgradeJsoncFrontMatterToLayout2(lines[1..fence.Close]);
            var outLines = new List<string> { lines[0] };
            outLines.AddRange(updated);
            outLines.AddRange(lines[fence.Close..]);
            return new TransformResult(outLines, Changed: true);
        }

        // Case 3: No front matter at all.
        if (!insertIfMissing)
        {
            return new TransformResult([.. lines], Changed: false);
        }

        var prepended = new List<string>
        {
            "```jsonc",
            "{",
            "  \"layout\": 2",
            "}",
            "```",
            "",
        };
        prepended.AddRange(lines);
        return new TransformResult(prepended, Changed: true);
    }

    private static List<string> UpgradeJsoncFrontMatterToLayout2(string[] blockLines)
    {
        var layoutRegex = new Regex(@"^(\s*""layout""\s*:\s*)[0-9]+(.*)$");
        var list = blockLines.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var m = layoutRegex.Match(list[i]);
            if (m.Success)
            {
                list[i] = $"{m.Groups[1].Value}2{m.Groups[2].Value}";
                return list;
            }
        }

        // Insert "layout": 2 right after the opening '{'.
        for (var i = 0; i < list.Count; i++)
        {
            var brace = list[i].IndexOf('{');
            if (brace < 0)
            {
                continue;
            }

            var afterBrace = list[i][(brace + 1)..].Trim();
            if (afterBrace.StartsWith('}'))
            {
                list[i] = list[i][..brace] + "{ \"layout\": 2 }" + afterBrace[1..];
                return list;
            }

            if (afterBrace.Length > 0 && !afterBrace.StartsWith("//", StringComparison.Ordinal))
            {
                list[i] = list[i][..(brace + 1)] + " \"layout\": 2," + list[i][(brace + 1)..];
                return list;
            }

            var hasNextEntry = list.Skip(i + 1).Any(l =>
            {
                var t = l.Trim();
                return t.Length > 0 && !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith('}');
            });
            list.Insert(i + 1, hasNextEntry ? "  \"layout\": 2," : "  \"layout\": 2");
            return list;
        }

        return ["{", "  \"layout\": 2", "}"];
    }

    // -------------------------------------------------------------------------
    // Spec migration (Layout 1 & Layout 2 -> Layout 2 JSONC)
    // -------------------------------------------------------------------------

    private TransformResult MigrateSpec(string[] lines)
    {
        var fm = MigrateFrontMatter(lines, forceLayout2: true, insertIfMissing: true);
        var cur = fm.Lines;
        var changed = fm.Changed;

        // Locate the first fenced gherkin block (loose CommonMark fence matching spec.ts).
        var doc = Doc.Split(string.Join('\n', cur));
        var gherkinFence = doc.Fences.FirstOrDefault(f => f.Info.Equals("gherkin", StringComparison.OrdinalIgnoreCase));
        if (gherkinFence is null)
        {
            return new TransformResult(cur, changed);
        }

        var close = gherkinFence.Close < 0 ? cur.Count : gherkinFence.Close;
        var blockLines = cur.GetRange(gherkinFence.Open + 1, close - (gherkinFence.Open + 1));
        var blockText = string.Join('\n', blockLines);

        // Already Layout 2 tagged? Leave Gherkin body untouched.
        if (SpecHasLayout2Tag.IsMatch(blockText))
        {
            return new TransformResult(cur, changed);
        }

        var rewritten = new List<string>(blockLines.Count);
        var gherkinChanged = false;

        foreach (var raw in blockLines)
        {
            var mRuleFrom = SpecRuleWithFrom.Match(raw);
            if (mRuleFrom.Success)
            {
                var indent = mRuleFrom.Groups[1].Value;
                var id = mRuleFrom.Groups[2].Value;
                var title = mRuleFrom.Groups[3].Value.Trim();
                var fromParts = mRuleFrom.Groups[4].Value
                    .Split(',')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Select(s => "@from:" + WhitespaceRun.Replace(s, "-"));
                var tags = string.Join(' ', [$"@{id}", .. fromParts]);
                rewritten.Add($"{indent}{tags}");
                rewritten.Add($"{indent}Rule: {title}");
                gherkinChanged = true;
                continue;
            }

            var mRulePlain = SpecRulePlain.Match(raw);
            if (mRulePlain.Success)
            {
                var indent = mRulePlain.Groups[1].Value;
                var id = mRulePlain.Groups[2].Value;
                var title = mRulePlain.Groups[3].Value.Trim();
                rewritten.Add($"{indent}@{id}");
                rewritten.Add($"{indent}Rule: {title}");
                gherkinChanged = true;
                continue;
            }

            var mSc = SpecScenario.Match(raw);
            if (mSc.Success)
            {
                var indent = mSc.Groups[1].Value;
                var outline = mSc.Groups[2].Success ? " Outline" : "";
                var id = mSc.Groups[3].Value;
                var title = mSc.Groups[4].Value.Trim();
                rewritten.Add($"{indent}@{id}");
                rewritten.Add($"{indent}Scenario{outline}: {title}");
                gherkinChanged = true;
                continue;
            }

            rewritten.Add(raw);
        }

        if (!gherkinChanged)
        {
            return new TransformResult(cur, changed);
        }

        var outLines = new List<string>(cur.Count + rewritten.Count - blockLines.Count);
        outLines.AddRange(cur.Take(gherkinFence.Open + 1));
        outLines.AddRange(rewritten);
        outLines.AddRange(cur.Skip(close));
        return new TransformResult(outLines, Changed: true);
    }

    // -------------------------------------------------------------------------
    // Plan migration (Layout 1 & Layout 2 -> Layout 2 JSONC)
    // -------------------------------------------------------------------------

    private TransformResult MigratePlan(string[] lines)
    {
        var fm = MigrateFrontMatter(lines, forceLayout2: true, insertIfMissing: true);
        var cur = fm.Lines;
        var changed = fm.Changed;

        var doc = Doc.Split(string.Join('\n', cur));
        var hasLayout1Tasks = Enumerable.Range(0, doc.Lines.Length)
            .Any(i => doc.Heading[i] && PlanHeading1.IsMatch(doc.Lines[i]));

        if (hasLayout1Tasks || HasLayout1SharedDecisionsTable(doc))
        {
            var upgraded = MigrateLayout1Plan(doc);
            if (upgraded.Changed)
            {
                cur = upgraded.Lines;
                changed = true;
                doc = Doc.Split(string.Join('\n', cur));
            }
        }

        // Convert any Layout 2 YAML record blocks (Shared decisions + task fields blocks) to JSONC.
        var yamlRewrites = FindPlanYamlRecordFences(doc);
        if (yamlRewrites.Count > 0)
        {
            cur = RewriteYamlFences(doc, yamlRewrites);
            changed = true;
        }

        return new TransformResult(cur, changed);
    }

    private static bool HasLayout1SharedDecisionsTable(Doc doc)
    {
        var heading = doc.NextHeading(0, doc.Lines.Length, PlanSharedHeading);
        if (heading >= doc.Lines.Length)
        {
            return false;
        }

        var end = doc.NextHeading(heading + 1, doc.Lines.Length, PlanTopHeading);
        if (doc.FieldsBlock(heading + 1, end) is not null || FindYamlBlock(doc, heading + 1, end) is not null)
        {
            return false;
        }

        for (var i = heading + 1; i < end; i++)
        {
            if (!doc.Fenced[i] && doc.Lines[i].TrimStart().StartsWith('|'))
            {
                return true;
            }
        }

        return false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private static TransformResult MigrateLayout1Plan(Doc doc)
    {
        var coversByTask = ExtractScenarioCoverage(doc);
        var replacements = new List<(int Start, int EndExclusive, List<string> Replacement)>();

        // 1. Shared decisions Markdown table -> JSONC array.
        var sharedHeading = doc.NextHeading(0, doc.Lines.Length, PlanSharedHeading);
        if (sharedHeading < doc.Lines.Length)
        {
            var sharedEnd = doc.NextHeading(sharedHeading + 1, doc.Lines.Length, PlanTopHeading);
            if (doc.FieldsBlock(sharedHeading + 1, sharedEnd) is null && FindYamlBlock(doc, sharedHeading + 1, sharedEnd) is null)
            {
                var tableStart = -1;
                var tableEnd = -1;
                var decisions = new List<Dictionary<string, string>>();
                for (var i = sharedHeading + 1; i < sharedEnd; i++)
                {
                    if (doc.Fenced[i])
                    {
                        continue;
                    }

                    var cells = PlanTableCells(doc.Lines[i]);
                    if (cells is null)
                    {
                        if (tableStart >= 0)
                        {
                            break;
                        }

                        continue;
                    }

                    if (tableStart < 0)
                    {
                        tableStart = i;
                    }

                    tableEnd = i + 1;
                    if (cells.Count < 3)
                    {
                        continue;
                    }

                    var question = cells[0];
                    var owner = cells[1];
                    var fn = cells[2];
                    if (TableSeparatorCell.IsMatch(question) ||
                        question.Equals("question", StringComparison.OrdinalIgnoreCase) ||
                        question.StartsWith('<'))
                    {
                        continue;
                    }

                    decisions.Add(new Dictionary<string, string>
                    {
                        ["question"] = question,
                        ["owner"] = owner,
                        ["function"] = fn,
                    });
                }

                if (tableStart >= 0 && tableEnd > tableStart)
                {
                    var block = new List<string> { "```jsonc" };
                    var json = JsonSerializer.Serialize(decisions, PrettyJsonOptions).Replace("\r\n", "\n");
                    block.AddRange(json.Split('\n'));
                    block.Add("```");
                    replacements.Add((tableStart, tableEnd, block));
                }
            }
        }

        // 2. Layout 1 tasks (`### T<n>: Name (\`repo\`)` + `- **Field:**` bullets).
        var heads = new List<(int Index, string Id, string Name, string Repo)>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (!doc.Heading[i])
            {
                continue;
            }

            var m = PlanHeading1.Match(doc.Lines[i]);
            if (m.Success)
            {
                heads.Add((i, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
            }
        }

        var priorIds = new List<string>();
        for (var n = 0; n < heads.Count; n++)
        {
            var h = heads[n];
            var limit = n + 1 < heads.Count ? heads[n + 1].Index : doc.Lines.Length;
            var end = doc.NextHeading(h.Index + 1, limit, PlanAnyHeading);

            // Preserve trailing blank lines that separate this task from the next heading.
            var contentEnd = end;
            while (contentEnd > h.Index + 1 && string.IsNullOrWhiteSpace(doc.Lines[contentEnd - 1]))
            {
                contentEnd--;
            }

            var bodyLines = doc.Lines[(h.Index + 1)..contentEnd];
            var dependsLine = bodyLines.FirstOrDefault(l => l.TrimStart().StartsWith("- **Depends on:**", StringComparison.Ordinal));
            List<string> dependsOn = dependsLine is null
                ? [.. priorIds]
                : NonAlphaNum.Split(dependsLine.TrimStart()["- **Depends on:**".Length..])
                    .Where(w => TaskIdToken.IsMatch(w))
                    .ToList();

            var sections = ParseBulletSections(bodyLines);

            var outTask = new List<string>
            {
                $"### {h.Id}: {h.Name}",
                "",
                "```jsonc",
                "{",
                $"  \"id\": {JsonSerializer.Serialize(h.Id, CompactJsonOptions)},",
                $"  \"repo\": {JsonSerializer.Serialize(h.Repo, CompactJsonOptions)},",
            };

            var covers = coversByTask.TryGetValue(h.Id, out var cList) && cList.Count > 0 ? cList : null;
            var depJson = "[" + string.Join(", ", dependsOn.Select(d => JsonSerializer.Serialize(d, CompactJsonOptions))) + "]";
            outTask.Add(covers is null
                ? $"  \"depends_on\": {depJson}"
                : $"  \"depends_on\": {depJson},");
            if (covers is not null)
            {
                var coversJson = "[" + string.Join(", ", covers.Select(c => JsonSerializer.Serialize(c, CompactJsonOptions))) + "]";
                outTask.Add($"  \"covers\": {coversJson}");
            }

            outTask.Add("}");
            outTask.Add("```");

            foreach (var sec in sections)
            {
                if (string.IsNullOrEmpty(sec.Title))
                {
                    if (!string.IsNullOrWhiteSpace(sec.Body))
                    {
                        outTask.Add("");
                        outTask.AddRange(sec.Body.Split('\n'));
                    }
                }
                else
                {
                    outTask.Add("");
                    outTask.Add($"#### {sec.Title}");
                    if (!string.IsNullOrWhiteSpace(sec.Body))
                    {
                        outTask.Add("");
                        outTask.AddRange(sec.Body.Split('\n'));
                    }
                }
            }

            replacements.Add((h.Index, contentEnd, outTask));
            priorIds.Add(h.Id);
        }

        if (replacements.Count == 0)
        {
            return new TransformResult([.. doc.Lines], Changed: false);
        }

        return new TransformResult(ApplyReplacements(doc.Lines, replacements), Changed: true);
    }

    private static Dictionary<string, List<string>> ExtractScenarioCoverage(Doc doc)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var heading = doc.NextHeading(0, doc.Lines.Length, PlanCoverageHeading);
        if (heading >= doc.Lines.Length)
        {
            return result;
        }

        var end = doc.NextHeading(heading + 1, doc.Lines.Length, PlanTopHeading);
        for (var i = heading + 1; i < end; i++)
        {
            if (doc.Fenced[i])
            {
                continue;
            }

            var cells = PlanTableCells(doc.Lines[i]);
            if (cells is null || cells.Count < 3)
            {
                continue;
            }

            var scenario = cells[0].Trim();
            if (!ScenarioIdRegex.IsMatch(scenario))
            {
                continue;
            }

            var taskIds = NonAlphaNum.Split(cells[^1])
                .Where(w => TaskIdToken.IsMatch(w))
                .Distinct(StringComparer.Ordinal);

            foreach (var taskId in taskIds)
            {
                if (!result.TryGetValue(taskId, out var list))
                {
                    list = [];
                    result[taskId] = list;
                }

                if (!list.Contains(scenario))
                {
                    list.Add(scenario);
                }
            }
        }

        return result;
    }

    private static List<(string Title, string Body)> ParseBulletSections(string[] lines)
    {
        var sections = new List<(string Title, List<string> Lines)> { ("", []) };
        foreach (var line in lines)
        {
            var m = PlanFieldBullet.Match(line);
            if (m.Success)
            {
                sections.Add((m.Groups[1].Value, [m.Groups[2].Value]));
            }
            else
            {
                var content = sections.Count > 1 ? StripOneOrTwoLeadingSpaces(line) : line;
                sections[^1].Lines.Add(content);
            }
        }

        return sections
            .Select(s => (s.Title, Body: string.Join('\n', s.Lines).Trim()))
            .Where(s => s.Title.Length > 0 || s.Body.Length > 0)
            .ToList();
    }

    private static string StripOneOrTwoLeadingSpaces(string line)
    {
        if (line.StartsWith("  ", StringComparison.Ordinal))
        {
            return line[2..];
        }

        if (line.StartsWith(' '))
        {
            return line[1..];
        }

        return line;
    }

    private static List<string>? PlanTableCells(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith('|'))
        {
            return null;
        }

        var cells = new List<string>();
        var cur = new StringBuilder();
        for (var i = 1; i < t.Length; i++)
        {
            var c = t[i];
            if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|')
            {
                cur.Append('|');
                i++;
            }
            else if (c == '|')
            {
                cells.Add(cur.ToString().Trim());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }

        if (cur.ToString().Trim().Length > 0)
        {
            cells.Add(cur.ToString().Trim());
        }

        return cells;
    }

    private static List<(Fence Fence, bool ExpectArray)> FindPlanYamlRecordFences(Doc doc)
    {
        var found = new List<(Fence Fence, bool ExpectArray)>();

        // Shared decisions YAML block.
        var sharedHeading = doc.NextHeading(0, doc.Lines.Length, PlanSharedHeading);
        if (sharedHeading < doc.Lines.Length)
        {
            var sharedEnd = doc.NextHeading(sharedHeading + 1, doc.Lines.Length, PlanTopHeading);
            var yamlFence = FindYamlBlock(doc, sharedHeading + 1, sharedEnd);
            if (yamlFence is not null)
            {
                found.Add((yamlFence, ExpectArray: true));
            }
        }

        // Task fields YAML blocks (first yaml fence right under `### T<n>: ...` before `#### ` or next heading).
        var heads = new List<int>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (doc.Heading[i] && PlanHeading2.IsMatch(doc.Lines[i]))
            {
                heads.Add(i);
            }
        }

        for (var n = 0; n < heads.Count; n++)
        {
            var start = heads[n];
            var limit = n + 1 < heads.Count ? heads[n + 1] : doc.Lines.Length;
            var end = doc.NextHeading(start + 1, limit, PlanAnyHeading);
            var fieldsLimit = doc.NextHeading(start + 1, end, PlanSectionHeading);
            var yamlFence = FindYamlBlock(doc, start + 1, fieldsLimit);
            if (yamlFence is not null)
            {
                found.Add((yamlFence, ExpectArray: false));
            }
        }

        return found;
    }

    // -------------------------------------------------------------------------
    // Build log migration (Layout 1 tables, hybrid, and Layout 2 YAML -> Layout 2 JSONC)
    // -------------------------------------------------------------------------

    private TransformResult MigrateBuildLog(string[] lines)
    {
        var fm = MigrateFrontMatter(lines, forceLayout2: true, insertIfMissing: true);
        var cur = fm.Lines;
        var changed = fm.Changed;

        var doc = Doc.Split(string.Join('\n', cur));
        var tableUpgraded = MigrateBuildLogTables(doc);
        if (tableUpgraded.Changed)
        {
            cur = tableUpgraded.Lines;
            changed = true;
            doc = Doc.Split(string.Join('\n', cur));
        }

        var yamlRewrites = FindBuildLogYamlRecordFences(doc);
        if (yamlRewrites.Count > 0)
        {
            cur = RewriteYamlFences(doc, yamlRewrites);
            changed = true;
        }

        return new TransformResult(cur, changed);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private static TransformResult MigrateBuildLogTables(Doc doc)
    {
        var replacements = new List<(int Start, int EndExclusive, List<string> Replacement)>();
        var i = 0;
        while (i < doc.Lines.Length)
        {
            if (doc.Fenced[i])
            {
                i++;
                continue;
            }

            var header = BuildTableCells(doc.Lines[i]);
            if (header is null || header.Count == 0 || !header[0].Equals("task", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            var commitsAt = 1;
            var departureAt = 2;
            var findCommits = header.FindIndex(c => c.Equals("commits", StringComparison.OrdinalIgnoreCase));
            var findDeparture = header.FindIndex(c => c.Equals("departure", StringComparison.OrdinalIgnoreCase));
            if (findCommits >= 0)
            {
                commitsAt = findCommits;
            }

            if (findDeparture >= 0)
            {
                departureAt = findDeparture;
            }

            var tableStart = i;
            var entries = new List<(string Task, List<string> Commits, string? Departure)>();
            i++;

            while (i < doc.Lines.Length && !doc.Fenced[i])
            {
                var row = BuildTableCells(doc.Lines[i]);
                if (row is null)
                {
                    break;
                }

                if (IsBuildTableSeparator(row))
                {
                    i++;
                    continue;
                }

                if (row.Count > 0 && TaskIdToken.IsMatch(row[0]))
                {
                    string Cell(int idx) => idx < row.Count ? row[idx] : "";
                    var last = departureAt + 1 == row.Count || departureAt >= row.Count;
                    var rawDep = (last ? Cell(departureAt) : string.Join('|', row.Skip(departureAt))).Trim();
                    var departure = rawDep.Length == 0 || rawDep.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : rawDep;
                    entries.Add((row[0], CommitsInCell(Cell(commitsAt)), departure));
                }

                i++;
            }

            var tableEnd = i;
            var rendered = new List<string>();
            for (var k = 0; k < entries.Count; k++)
            {
                var e = entries[k];
                if (k > 0)
                {
                    rendered.Add("");
                }

                rendered.Add($"### {e.Task}");
                rendered.Add("");
                rendered.Add("```jsonc");
                rendered.Add("{");
                rendered.Add($"  \"task\": {JsonSerializer.Serialize(e.Task, CompactJsonOptions)},");
                var commitsJson = "[" + string.Join(", ", e.Commits.Select(c => JsonSerializer.Serialize(c, CompactJsonOptions))) + "]";
                if (e.Departure is null)
                {
                    rendered.Add($"  \"commits\": {commitsJson}");
                }
                else
                {
                    rendered.Add($"  \"commits\": {commitsJson},");
                    rendered.Add($"  \"departure\": {JsonSerializer.Serialize(e.Departure, CompactJsonOptions)}");
                }

                rendered.Add("}");
                rendered.Add("```");
            }

            replacements.Add((tableStart, tableEnd, rendered));
        }

        if (replacements.Count == 0)
        {
            return new TransformResult([.. doc.Lines], Changed: false);
        }

        return new TransformResult(ApplyReplacements(doc.Lines, replacements), Changed: true);
    }

    private static List<string>? BuildTableCells(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith('|'))
        {
            return null;
        }

        var inner = t[1..];
        if (inner.EndsWith('|'))
        {
            inner = inner[..^1];
        }

        return inner.Split('|').Select(c => c.Trim()).ToList();
    }

    private static bool IsBuildTableSeparator(List<string> row) =>
        row.Count > 0 && row.All(c => c.Length > 0 && BuildTableSeparatorCell.IsMatch(c));

    private static List<string> CommitsInCell(string cell)
    {
        IEnumerable<string> parts = cell.Contains('`')
            ? cell.Split('`').Where((_, idx) => idx % 2 == 1)
            : cell.Split(',');
        return parts.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    private static List<(Fence Fence, bool ExpectArray)> FindBuildLogYamlRecordFences(Doc doc)
    {
        var found = new List<(Fence Fence, bool ExpectArray)>();
        var heads = new List<int>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (doc.Heading[i] && BuildEntryHeading.IsMatch(doc.Lines[i]))
            {
                heads.Add(i);
            }
        }

        for (var n = 0; n < heads.Count; n++)
        {
            var start = heads[n];
            var limit = n + 1 < heads.Count ? heads[n + 1] : doc.Lines.Length;
            var end = doc.NextHeading(start + 1, limit, PlanAnyHeading);
            var yamlFence = FindYamlBlock(doc, start + 1, end);
            if (yamlFence is not null)
            {
                found.Add((yamlFence, ExpectArray: false));
            }
        }

        return found;
    }

    // -------------------------------------------------------------------------
    // Review migration (Layout 2 YAML -> Layout 2 JSONC)
    // -------------------------------------------------------------------------

    private TransformResult MigrateReview(string[] lines)
    {
        var fm = MigrateFrontMatter(lines, forceLayout2: true, insertIfMissing: true);
        var cur = fm.Lines;
        var changed = fm.Changed;

        var doc = Doc.Split(string.Join('\n', cur));
        var yamlRewrites = FindReviewYamlRecordFences(doc);
        if (yamlRewrites.Count > 0)
        {
            cur = RewriteYamlFences(doc, yamlRewrites);
            changed = true;
        }

        return new TransformResult(cur, changed);
    }

    private static List<(Fence Fence, bool ExpectArray)> FindReviewYamlRecordFences(Doc doc)
    {
        var found = new List<(Fence Fence, bool ExpectArray)>();
        var reviews = new List<int>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (doc.Heading[i] && ReviewHeading.IsMatch(doc.Lines[i]))
            {
                reviews.Add(i);
            }
        }

        for (var r = 0; r < reviews.Count; r++)
        {
            var revStart = reviews[r];
            var revEnd = doc.NextHeading(revStart + 1, doc.Lines.Length, PlanTopHeading);
            var firstFinding = doc.NextHeading(revStart + 1, revEnd, FindingHeading);

            var headerFence = FindYamlBlock(doc, revStart + 1, firstFinding);
            if (headerFence is not null)
            {
                found.Add((headerFence, ExpectArray: false));
            }

            var findings = new List<int>();
            for (var i = firstFinding; i < revEnd; i++)
            {
                if (doc.Heading[i] && FindingHeading.IsMatch(doc.Lines[i]))
                {
                    findings.Add(i);
                }
            }

            for (var f = 0; f < findings.Count; f++)
            {
                var fStart = findings[f];
                var bound = f + 1 < findings.Count ? findings[f + 1] : revEnd;
                var fEnd = doc.NextHeading(fStart + 1, bound, PlanAnyHeading);
                var findingFence = FindYamlBlock(doc, fStart + 1, fEnd);
                if (findingFence is not null)
                {
                    found.Add((findingFence, ExpectArray: false));
                }
            }
        }

        return found;
    }

    // -------------------------------------------------------------------------
    // Shared YAML-fence helpers
    // -------------------------------------------------------------------------

    private static bool IsYamlFence(Fence f) =>
        f.Info.Equals("yaml", StringComparison.OrdinalIgnoreCase) ||
        f.Info.Equals("yml", StringComparison.OrdinalIgnoreCase);

    private static Fence? FindYamlBlock(Doc doc, int from, int limit) =>
        doc.Fences.FirstOrDefault(f => IsYamlFence(f) && f.Open >= from && f.Open < limit);

    private List<string> RewriteYamlFences(Doc doc, List<(Fence Fence, bool ExpectArray)> targets)
    {
        var replacements = new List<(int Start, int EndExclusive, List<string> Replacement)>();
        foreach (var (fence, expectArray) in targets.OrderBy(t => t.Fence.Open))
        {
            var close = fence.Close < 0 ? doc.Lines.Length : fence.Close;
            var openLine = doc.Lines[fence.Open];
            var indentLen = openLine.Length - openLine.TrimStart().Length;
            var indent = openLine[..indentLen];
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

    private static List<string> ApplyReplacements(string[] lines, List<(int Start, int EndExclusive, List<string> Replacement)> replacements)
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
}
