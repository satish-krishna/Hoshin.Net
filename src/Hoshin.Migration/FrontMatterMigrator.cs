using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Migration;

internal static partial class FrontMatterMigrator
{
    [GeneratedRegex(@"^(\s*""layout""\s*:\s*)[0-9]+(.*)$")]
    private static partial Regex LayoutFieldRegex();

    public static MigrationTransformResult Migrate(IYamlToJsoncConverter yaml, string[] lines, bool forceLayout2, bool insertIfMissing)
    {
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var close = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            if (close < 0)
            {
                throw new FormatException("YAML front matter is never closed");
            }

            var yamlBody = string.Join('\n', lines[1..close]);
            var jsonc = yaml.ConvertFrontMatter(yamlBody, forceLayout2);
            var outLines = new List<string> { "```jsonc" };
            outLines.AddRange(jsonc.Split('\n'));
            outLines.Add("```");
            outLines.AddRange(lines[(close + 1)..]);
            return new MigrationTransformResult(outLines, Changed: true);
        }

        var doc = Doc.Split(string.Join('\n', lines));
        var fence = doc.Fences.FirstOrDefault(f => f.Open == 0 && Doc.IsFieldsBlock(f));
        if (fence is not null && fence.Close > 0)
        {
            return UpgradeExistingJsoncFrontMatter(lines, doc, fence, forceLayout2);
        }

        if (!insertIfMissing)
        {
            return new MigrationTransformResult([.. lines], Changed: false);
        }

        var prepended = new List<string> { "```jsonc", "{", "  \"layout\": 2", "}", "```", "" };
        prepended.AddRange(lines);
        return new MigrationTransformResult(prepended, Changed: true);
    }

    private static MigrationTransformResult UpgradeExistingJsoncFrontMatter(string[] lines, Doc doc, Fence fence, bool forceLayout2)
    {
        if (!forceLayout2)
        {
            return new MigrationTransformResult([.. lines], Changed: false);
        }

        var block = doc.BlockOf(fence);
        if (Jsonc.TryParse(block.Text, out var parsed, out _) &&
            parsed is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty("layout", out var lp) &&
            lp.ValueKind == JsonValueKind.Number &&
            lp.TryGetInt32(out var ver) && ver >= 2)
        {
            return new MigrationTransformResult([.. lines], Changed: false);
        }

        var updated = UpgradeJsoncFrontMatterToLayout2(lines[1..fence.Close]);
        var outLines = new List<string> { lines[0] };
        outLines.AddRange(updated);
        outLines.AddRange(lines[fence.Close..]);
        return new MigrationTransformResult(outLines, Changed: true);
    }

    private static List<string> UpgradeJsoncFrontMatterToLayout2(string[] blockLines)
    {
        var list = blockLines.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var m = LayoutFieldRegex().Match(list[i]);
            if (m.Success)
            {
                list[i] = $"{m.Groups[1].Value}2{m.Groups[2].Value}";
                return list;
            }
        }

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
}
