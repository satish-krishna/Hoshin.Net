using System.Text;
using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class FrontMatterEditTests
{
    private static List<(string Name, JsonValueKind Kind, string? StringValue)> ReadTopLevelProperties(string markdown)
    {
        var doc = Doc.Split(markdown);
        var fence = doc.Fences.First(f => f.Open == 0 && Doc.IsFieldsBlock(f));
        var bytes = Encoding.UTF8.GetBytes(doc.BlockOf(fence).Text);
        var reader = new Utf8JsonReader(bytes, Jsonc.ReaderOptions);
        var list = new List<(string, JsonValueKind, string?)>();

        Assert.True(reader.Read());
        Assert.Equal(JsonTokenType.StartObject, reader.TokenType);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Assert.Equal(JsonTokenType.PropertyName, reader.TokenType);
            var name = reader.GetString()!;
            Assert.True(reader.Read());
            var kind = reader.TokenType switch
            {
                JsonTokenType.String => JsonValueKind.String,
                JsonTokenType.Number => JsonValueKind.Number,
                JsonTokenType.True => JsonValueKind.True,
                JsonTokenType.False => JsonValueKind.False,
                JsonTokenType.Null => JsonValueKind.Null,
                JsonTokenType.StartObject => JsonValueKind.Object,
                JsonTokenType.StartArray => JsonValueKind.Array,
                _ => JsonValueKind.Undefined,
            };
            var str = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            list.Add((name, kind, str));
        }

        return list;
    }

    [Fact]
    public void Set_ExistingStringValue_UpdatesInPlaceWithoutDuplicateKey()
    {
        using var tmp = new TempDir("hoshin-t13-str-");
        var path = Path.Combine(tmp.Path, "intent.md");
        File.WriteAllText(path, "```jsonc\n{\n  \"layout\": 2,\n  \"status\": \"draft\"\n}\n```\n\n# Intent\n");

        FrontMatterEdit.Set(path, "status", "accepted");

        var updated = File.ReadAllText(path);
        var props = ReadTopLevelProperties(updated);
        var matching = props.Where(p => p.Name == "status").ToList();
        Assert.Single(matching);
        Assert.Equal("accepted", matching[0].StringValue);
        Assert.DoesNotContain("\r\n", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_ExistingNullValue_ReplacesNullWithoutDuplicateKey()
    {
        using var tmp = new TempDir("hoshin-t13-null-");
        var path = Path.Combine(tmp.Path, "spec.md");
        File.WriteAllText(path, "```jsonc\n{\n  \"layout\": 2,\n  \"source_intent_commit\": null,\n  \"status\": \"draft\"\n}\n```\n\n# Spec\n");

        FrontMatterEdit.Set(path, "source_intent_commit", "0123456789abcdef");

        var updated = File.ReadAllText(path);
        var props = ReadTopLevelProperties(updated);
        var matching = props.Where(p => p.Name == "source_intent_commit").ToList();
        Assert.Single(matching);
        Assert.Equal("0123456789abcdef", matching[0].StringValue);
        Assert.Equal("0123456789abcdef", Jsonc.String(FrontMatter.Parse(updated, "spec.md").Data, "source_intent_commit"));
    }

    [Fact]
    public void Set_ExistingNumberValue_ReplacesNumberWithoutDuplicateKey()
    {
        using var tmp = new TempDir("hoshin-t13-num-");
        var path = Path.Combine(tmp.Path, "spec.md");
        File.WriteAllText(path, "```jsonc\n{\n  \"layout\": 2,\n  \"source_intent_commit\": 1234567\n}\n```\n\n# Spec\n");

        FrontMatterEdit.Set(path, "source_intent_commit", "abcdef0");

        var updated = File.ReadAllText(path);
        var props = ReadTopLevelProperties(updated);
        var matching = props.Where(p => p.Name == "source_intent_commit").ToList();
        Assert.Single(matching);
        Assert.Equal(JsonValueKind.String, matching[0].Kind);
        Assert.Equal("abcdef0", matching[0].StringValue);
    }

    [Fact]
    public void Set_MissingKey_InsertsBeforeClosingBraceMatchingIndentationAndTrailingComma()
    {
        using var tmp = new TempDir("hoshin-t13-missing-");

        // Multi-line without trailing comma
        var p1 = Path.Combine(tmp.Path, "a.md");
        File.WriteAllText(p1, "```jsonc\n{\n  \"layout\": 2,\n  \"system\": \"demo\"\n}\n```\n");
        FrontMatterEdit.Set(p1, "source_intent_commit", "abc1234");
        var u1 = File.ReadAllText(p1);
        var props1 = ReadTopLevelProperties(u1);
        var prop1 = Assert.Single(props1, p => p.Name == "source_intent_commit");
        Assert.Equal("abc1234", prop1.StringValue);
        Assert.Contains("\n  \"source_intent_commit\": \"abc1234\"\n}", u1, StringComparison.Ordinal);

        // Multi-line with trailing comma
        var p2 = Path.Combine(tmp.Path, "b.md");
        File.WriteAllText(p2, "```jsonc\n{\n  \"layout\": 2,\n  \"system\": \"demo\",\n}\n```\n");
        FrontMatterEdit.Set(p2, "source_intent_commit", "def5678");
        var u2 = File.ReadAllText(p2);
        var props2 = ReadTopLevelProperties(u2);
        var prop2 = Assert.Single(props2, p => p.Name == "source_intent_commit");
        Assert.Equal("def5678", prop2.StringValue);
        Assert.Contains("\n  \"source_intent_commit\": \"def5678\",\n}", u2, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_KeyMentionedInCommentAboveRealKey_LeavesCommentUntouchedAndUpdatesRealKey()
    {
        using var tmp = new TempDir("hoshin-t13-comment-");
        var path = Path.Combine(tmp.Path, "intent.md");
        const string content = """
            ```jsonc
            {
              // was "status": "draft"
              "status": "draft",
              "system": "demo"
            }
            ```

            # Intent
            """;
        File.WriteAllText(path, content);

        FrontMatterEdit.Set(path, "status", "accepted");

        var updated = File.ReadAllText(path);
        Assert.Contains("// was \"status\": \"draft\"", updated, StringComparison.Ordinal);
        var props = ReadTopLevelProperties(updated);
        var matching = props.Where(p => p.Name == "status").ToList();
        Assert.Single(matching);
        Assert.Equal("accepted", matching[0].StringValue);
    }

    [Fact]
    public void Set_CrlfFile_PreservesCrlfLineEndingsEverywhere()
    {
        using var tmp = new TempDir("hoshin-t13-crlf-");
        var path = Path.Combine(tmp.Path, "intent.md");
        var content = "```jsonc\r\n{\r\n  \"layout\": 2,\r\n  \"status\": \"draft\"\r\n}\r\n```\r\n\r\n# Intent\r\nBody line.\r\n";
        File.WriteAllText(path, content);

        FrontMatterEdit.Set(path, "status", "accepted");
        FrontMatterEdit.Set(path, "source_intent_commit", "deadbeef");

        var updated = File.ReadAllText(path);
        Assert.Contains("\r\n", updated, StringComparison.Ordinal);
        var withoutCrlf = updated.Replace("\r\n", "", StringComparison.Ordinal);
        Assert.DoesNotContain("\n", withoutCrlf, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", withoutCrlf, StringComparison.Ordinal);

        var props = ReadTopLevelProperties(updated);
        Assert.Equal("accepted", props.Single(p => p.Name == "status").StringValue);
        Assert.Equal("deadbeef", props.Single(p => p.Name == "source_intent_commit").StringValue);
    }
}
