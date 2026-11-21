using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Documents;

public sealed record FrontMatterData(JsonElement Data, int Layout, bool HasLayoutField, int BodyLine);

/// <summary>Reads the fenced <c>```jsonc</c> front matter block on line 1 of a document. YAML front matter is refused.</summary>
public static class FrontMatter
{
    private static readonly Regex LineEndRegex = new(@"\r\n|\r|\n", RegexOptions.Compiled);

    public static FrontMatterData Parse(string text, string file) => Parse(Doc.Split(text), file);

    public static FrontMatterData Parse(Doc doc, string file)
    {
        if (doc.Lines.Length > 0 && doc.Lines[0].Trim() == "---")
        {
            throw new CouldNotRunException($"{file}: front matter is YAML; put it in a ```jsonc block on the first line");
        }

        var fence = doc.Fences.FirstOrDefault(f => f.Open == 0 && Doc.IsFieldsBlock(f));
        if (fence is null)
        {
            return new FrontMatterData(Jsonc.EmptyObject(), 1, HasLayoutField: false, BodyLine: 1);
        }

        if (fence.Close < 0)
        {
            throw new CouldNotRunException($"{file}: front matter is never closed");
        }

        if (!Jsonc.TryParse(doc.BlockOf(fence).Text, out var value, out var error))
        {
            throw new CouldNotRunException($"{file}: front matter is not valid JSONC: {error}");
        }

        JsonElement data;
        if (value is null)
        {
            data = Jsonc.EmptyObject();
        }
        else if (value.Value.ValueKind == JsonValueKind.Object)
        {
            data = value.Value;
        }
        else
        {
            throw new CouldNotRunException($"{file}: front matter is not a set of fields");
        }

        if (!data.TryGetProperty("layout", out var layoutProp) || layoutProp.ValueKind == JsonValueKind.Null)
        {
            return new FrontMatterData(data, 1, HasLayoutField: false, BodyLine: fence.Close + 2);
        }

        if (layoutProp.ValueKind != JsonValueKind.Number || !layoutProp.TryGetInt32(out var layout) || layout < 1)
        {
            throw new CouldNotRunException($"layout must be a whole number of 1 or more, found {layoutProp.GetRawText()}");
        }

        return new FrontMatterData(data, layout, HasLayoutField: true, BodyLine: fence.Close + 2);
    }

    public static void RefuseNewerLayout(int layout, string file)
    {
        if (layout > 2)
        {
            throw new CouldNotRunException($"{file}: layout {layout} is newer than this program reads (it reads 2); update the hoshin plugin");
        }
    }

    public static void RefuseUnsupportedLayout(int layout, string file)
    {
        RefuseNewerLayout(layout, file);
        if (layout != 2)
        {
            throw new CouldNotRunException($"{file}: layout {layout} is not read by this program; migrate it to layout 2 JSONC");
        }
    }

    /// <summary>
    /// The 1-based closing line of the line-1 JSONC front matter block, counted in lines ended by <c>\n</c> as git
    /// counts them; 0 when the text has no closed JSONC front matter on line 1.
    /// </summary>
    public static int CloseLine(string text)
    {
        var doc = Doc.Split(text);
        var fence = doc.Fences.FirstOrDefault(f => f.Open == 0 && Doc.IsFieldsBlock(f));
        if (fence is null || fence.Close < 0)
        {
            return 0;
        }

        var parts = Regex.Split(text, @"(\r\n|\r|\n)");
        var offset = 0;
        var limit = Math.Min(fence.Close * 2, parts.Length);
        for (var i = 0; i < limit; i++)
        {
            offset += parts[i].Length;
        }

        return text[..offset].Split('\n').Length;
    }

    /// <summary>Reads one front matter property as a raw JsonElement; null when missing or front matter cannot be parsed.</summary>
    public static JsonElement? TryGetField(string text, string file, string field)
    {
        try
        {
            var data = Parse(text, file).Data;
            return data.TryGetProperty(field, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads one front matter property as string text; null when missing, null, or unreadable.</summary>
    public static string? TryGetScalar(string text, string file, string field)
    {
        try
        {
            return Jsonc.ScalarText(Parse(text, file).Data, field);
        }
        catch
        {
            return null;
        }
    }
}
