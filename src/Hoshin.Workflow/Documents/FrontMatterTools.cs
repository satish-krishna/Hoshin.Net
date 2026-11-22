using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Documents;

/// <summary>Checks the shape of a document's front matter before any reader runs, reporting actionable guidance.</summary>
public static class FrontMatterCheck
{
    public static Problem? Check(Doc doc, string file)
    {
        if (doc.Fences.Any(f => f.Open == 0 && Doc.IsFieldsBlock(f)))
        {
            return null;
        }

        var first = doc.Lines.Length > 0 ? doc.Lines[0].Trim() : "";
        return first switch
        {
            _ when first.StartsWith('{') => new Problem(file, 1, "the front matter is bare JSON: put a line with ```jsonc before it and a line with ``` after it, so the file starts with the ```jsonc line"),
            "---" => new Problem(file, 1, "the front matter is YAML: write it as JSON inside a ```jsonc block that starts on the first line"),
            _ => new Problem(file, 1, "the front matter is missing: put a ```jsonc block on the first line"),
        };
    }
}

/// <summary>Updates a string field inside a document's line-1 <c>```jsonc</c> front matter in place, preserving all other lines.</summary>
public static partial class FrontMatterEdit
{
    [GeneratedRegex(@"""([^""]+)""\s*:\s*""(?:[^""\\]|\\.)*""")]
    private static partial Regex StringFieldRegex();

    public static void Set(string path, string field, string value)
    {
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        var close = Array.FindIndex(lines, 1, l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        if (lines.Length == 0 || !lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal) || close < 0)
        {
            throw new CouldNotRunException($"{Path.GetFileName(path)} has no jsonc front matter to set {field} in");
        }

        var encoded = JsonSerializer.Serialize(value);
        for (var i = 1; i < close; i++)
        {
            foreach (Match m in StringFieldRegex().Matches(lines[i]))
            {
                if (string.Equals(m.Groups[1].Value, field, StringComparison.Ordinal))
                {
                    lines[i] = lines[i][..m.Index] + $"\"{field}\": {encoded}" + lines[i][(m.Index + m.Length)..];
                    File.WriteAllText(path, string.Join('\n', lines));
                    return;
                }
            }
        }

        var brace = Array.FindIndex(lines, 1, l => l.Contains('{'));
        if (brace < 0 || brace >= close)
        {
            throw new CouldNotRunException($"{Path.GetFileName(path)} front matter is not an object");
        }

        var at = lines[brace].IndexOf('{');
        lines[brace] = lines[brace][..(at + 1)] + $" \"{field}\": {encoded}," + lines[brace][(at + 1)..];
        File.WriteAllText(path, string.Join('\n', lines));
    }
}
