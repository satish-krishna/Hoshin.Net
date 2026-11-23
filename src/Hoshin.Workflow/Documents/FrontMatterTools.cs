using System.Text;
using System.Text.Json;
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

/// <summary>Updates a string field inside a document's line-1 <c>```jsonc</c> front matter using real JSONC tokens, preserving comments and line endings.</summary>
public sealed class FrontMatterEdit(IFileSystem fileSystem)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    private sealed record ObjectScan(
        int OpenBraceByte,
        int CloseBraceByte,
        int? FoundValueStart,
        int? FoundValueEnd,
        int? FirstPropertyStart,
        int? LastValueEnd);

    public void Set(string path, string field, string value)
    {
        var rawText = _fs.ReadAllText(path);
        var doc = Doc.Split(rawText);
        var fence = doc.Fences.FirstOrDefault(f => f.Open == 0 && Doc.IsFieldsBlock(f));
        if (fence is null || fence.Close < 0)
        {
            throw new CouldNotRunException($"{Path.GetFileName(path)} has no jsonc front matter to set {field} in");
        }

        var lineStarts = GetLineStarts(rawText);
        var blockStart = lineStarts[1];
        var blockEnd = lineStarts[fence.Close];
        var blockText = rawText[blockStart..blockEnd];
        var newline = DetectNewline(rawText);

        var utf8 = Utf8NoBom.GetBytes(blockText);
        var scan = ScanTopLevelObject(utf8, field, Path.GetFileName(path));
        var encodedValue = JsonSerializer.Serialize(value);

        byte[] updatedBlock;
        if (scan.FoundValueStart is int valStart && scan.FoundValueEnd is int valEnd)
        {
            updatedBlock = ReplaceSlice(utf8, valStart, valEnd, Utf8NoBom.GetBytes(encodedValue));
        }
        else
        {
            var encodedKey = JsonSerializer.Serialize(field);
            updatedBlock = InsertMissingProperty(utf8, scan, encodedKey, encodedValue, newline);
        }

        var updatedText = string.Concat(rawText.AsSpan(0, blockStart), Utf8NoBom.GetString(updatedBlock), rawText.AsSpan(blockEnd));
        _fs.WriteAllTextAtomic(path, updatedText);
    }

    private static ObjectScan ScanTopLevelObject(byte[] utf8, string targetField, string fileName)
    {
        var reader = new Utf8JsonReader(utf8, Jsonc.ReaderOptions);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new CouldNotRunException($"{fileName} front matter is not an object");
            }

            var openBrace = (int)reader.TokenStartIndex;
            int? foundStart = null;
            int? foundEnd = null;
            int? firstPropStart = null;
            int? lastValueEnd = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return new ObjectScan(openBrace, (int)reader.TokenStartIndex, foundStart, foundEnd, firstPropStart, lastValueEnd);
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var propStart = (int)reader.TokenStartIndex;
                firstPropStart ??= propStart;
                var name = reader.GetString();

                if (!reader.Read())
                {
                    throw new CouldNotRunException($"{fileName} front matter ended unexpectedly after property {name}");
                }

                var valueStart = (int)reader.TokenStartIndex;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }

                var valueEnd = (int)reader.BytesConsumed;
                lastValueEnd = valueEnd;

                if (foundStart is null && string.Equals(name, targetField, StringComparison.Ordinal))
                {
                    foundStart = valueStart;
                    foundEnd = valueEnd;
                }
            }

            throw new CouldNotRunException($"{fileName} front matter object is never closed");
        }
        catch (JsonException e)
        {
            throw new CouldNotRunException($"{fileName} front matter is not valid JSONC: {e.Message.Split('\n')[0]}");
        }
    }

    private static byte[] InsertMissingProperty(byte[] utf8, ObjectScan scan, string encodedKey, string encodedValue, string newline)
    {
        var isMultiLine = utf8.AsSpan(scan.OpenBraceByte, scan.CloseBraceByte - scan.OpenBraceByte).IndexOf((byte)'\n') >= 0;
        var pair = $"{encodedKey}: {encodedValue}";

        if (!isMultiLine)
        {
            return InsertSingleLine(utf8, scan, pair);
        }

        var indent = DetectPropertyIndent(utf8, scan.FirstPropertyStart);
        var closeLineStart = FindLineStart(utf8, scan.CloseBraceByte);

        if (scan.LastValueEnd is not int lastEnd)
        {
            var insert = Utf8NoBom.GetBytes($"{indent}{pair}{newline}");
            return ReplaceSlice(utf8, closeLineStart, closeLineStart, insert);
        }

        var hasTrailingComma = HasCommaBetween(utf8, lastEnd, scan.CloseBraceByte);
        if (closeLineStart < lastEnd)
        {
            var inlineInsert = hasTrailingComma
                ? Utf8NoBom.GetBytes($"{newline}{indent}{pair},")
                : Utf8NoBom.GetBytes($",{newline}{indent}{pair}");
            return hasTrailingComma
                ? ReplaceSlice(utf8, scan.CloseBraceByte, scan.CloseBraceByte, inlineInsert)
                : ReplaceSlice(utf8, lastEnd, lastEnd, inlineInsert);
        }

        if (hasTrailingComma)
        {
            var insert = Utf8NoBom.GetBytes($"{indent}{pair},{newline}");
            return ReplaceSlice(utf8, closeLineStart, closeLineStart, insert);
        }

        var comma = Utf8NoBom.GetBytes(",");
        var propLine = Utf8NoBom.GetBytes($"{indent}{pair}{newline}");
        using var ms = new MemoryStream(utf8.Length + comma.Length + propLine.Length);
        ms.Write(utf8, 0, lastEnd);
        ms.Write(comma);
        ms.Write(utf8, lastEnd, closeLineStart - lastEnd);
        ms.Write(propLine);
        ms.Write(utf8, closeLineStart, utf8.Length - closeLineStart);
        return ms.ToArray();
    }

    private static byte[] InsertSingleLine(byte[] utf8, ObjectScan scan, string pair)
    {
        if (scan.LastValueEnd is not int lastEnd)
        {
            var insert = Utf8NoBom.GetBytes($" {pair} ");
            return ReplaceSlice(utf8, scan.CloseBraceByte, scan.CloseBraceByte, insert);
        }

        if (HasCommaBetween(utf8, lastEnd, scan.CloseBraceByte))
        {
            var insert = Utf8NoBom.GetBytes($" {pair}, ");
            return ReplaceSlice(utf8, scan.CloseBraceByte, scan.CloseBraceByte, insert);
        }

        var appended = Utf8NoBom.GetBytes($", {pair}");
        return ReplaceSlice(utf8, lastEnd, lastEnd, appended);
    }

    private static bool HasCommaBetween(byte[] utf8, int start, int end)
    {
        var i = start;
        while (i < end)
        {
            var b = utf8[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                i++;
                continue;
            }

            if (b == (byte)'/' && i + 1 < end && utf8[i + 1] == (byte)'/')
            {
                i += 2;
                while (i < end && utf8[i] != (byte)'\n')
                {
                    i++;
                }

                continue;
            }

            if (b == (byte)'/' && i + 1 < end && utf8[i + 1] == (byte)'*')
            {
                i += 2;
                while (i + 1 < end && !(utf8[i] == (byte)'*' && utf8[i + 1] == (byte)'/'))
                {
                    i++;
                }

                i = Math.Min(end, i + 2);
                continue;
            }

            return b == (byte)',';
        }

        return false;
    }

    private static string DetectPropertyIndent(byte[] utf8, int? firstPropertyStart)
    {
        if (firstPropertyStart is not int propStart)
        {
            return "  ";
        }

        var lineStart = FindLineStart(utf8, propStart);
        for (var i = lineStart; i < propStart; i++)
        {
            if (utf8[i] is not ((byte)' ' or (byte)'\t'))
            {
                return "  ";
            }
        }

        return propStart > lineStart ? Utf8NoBom.GetString(utf8, lineStart, propStart - lineStart) : "  ";
    }

    private static int FindLineStart(byte[] utf8, int offset)
    {
        for (var i = offset - 1; i >= 0; i--)
        {
            if (utf8[i] == (byte)'\n')
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static byte[] ReplaceSlice(byte[] source, int start, int end, byte[] replacement)
    {
        var result = new byte[source.Length - (end - start) + replacement.Length];
        Buffer.BlockCopy(source, 0, result, 0, start);
        Buffer.BlockCopy(replacement, 0, result, start, replacement.Length);
        Buffer.BlockCopy(source, end, result, start + replacement.Length, source.Length - end);
        return result;
    }

    private static List<int> GetLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static string DetectNewline(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
