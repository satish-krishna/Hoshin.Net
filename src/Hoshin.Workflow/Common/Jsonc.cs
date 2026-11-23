using System.Text.Json;

namespace Hoshin.Workflow.Common;

/// <summary>JSON with comments and trailing commas, read natively by System.Text.Json.</summary>
public static class Jsonc
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses JSONC. An empty or comment-only text returns <c>true</c> with <paramref name="value"/> set to <c>null</c>.</summary>
    public static bool TryParse(string text, out JsonElement? value, out string error)
    {
        value = null;
        error = "";
        if (IsBlank(text))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, DocumentOptions);
            value = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException e)
        {
            error = e.Message.Split('\n')[0];
            return false;
        }
    }

    private static bool IsBlank(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i += 2;
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (text[i + 1] == '*')
                {
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return false;
                    }

                    i = close + 2;
                    continue;
                }
            }

            return false;
        }

        return true;
    }

    public static string? String(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Reads a scalar property as its text form (string value, or raw text for numbers/booleans); null when absent or null.</summary>
    public static string? ScalarText(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static List<string> Strings(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    /// <summary>Reads an array of commit hashes (strings or integers) as strings.</summary>
    public static List<string> TextList(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString()!);
            }
            else if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out _))
            {
                result.Add(item.GetRawText());
            }
        }

        return result;
    }

    /// <summary>An argv list: a non-empty array of non-empty strings, else null.</summary>
    public static string[]? Argv(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0)
        {
            return null;
        }

        var items = v.EnumerateArray().ToList();
        return items.All(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Length > 0)
            ? items.Select(x => x.GetString()!).ToArray()
            : null;
    }

    public static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
