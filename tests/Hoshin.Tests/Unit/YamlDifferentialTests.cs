using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Migration;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class YamlDifferentialTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SiblingYamlMjs = Path.GetFullPath(Path.Combine(RepoRoot, "..", "hoshin", "tools", "vendor", "yaml.mjs"));

    public sealed record Sample(string Name, string Yaml, string? ExpectedJson, bool ExpectRefusal = false, bool UseStringKeys = false);

    public static IReadOnlyList<Sample> Samples { get; } =
    [
        new("01_simple_mapping", "b: 2\na: 1\nc: hello\n", """{"b":2,"a":1,"c":"hello"}"""),
        new("02_literal_clip", "body: |\n  line1\n  line2\n", """{"body":"line1\nline2\n"}"""),
        new("03_literal_strip", "body: |-\n  line1\n  line2\n", """{"body":"line1\nline2"}"""),
        new("04_literal_keep", "body: |+\n  line1\n  line2\n\n", """{"body":"line1\nline2\n\n"}"""),
        new("05_literal_trailing_spaces", "body: |-\n  keep   \n  end\n", """{"body":"keep   \nend"}"""),
        new("06_folded_clip", "body: >\n  hello\n  world\n", """{"body":"hello world\n"}"""),
        new("07_folded_strip", "body: >-\n  hello\n  world\n", """{"body":"hello world"}"""),
        new("08_flow_sequence", "items: [a, b, c]\n", """{"items":["a","b","c"]}"""),
        new("09_flow_mapping", "meta: { a: 1, b: two }\n", """{"meta":{"a":1,"b":"two"}}"""),
        new("10_nested_flow", "meta: { a: [1, { b: true }] }\n", """{"meta":{"a":[1,{"b":true}]}}"""),
        new("11_single_quoted_escape", "msg: 'it''s fine'\n", """{"msg":"it's fine"}"""),
        new("12_double_quoted_escapes", "msg: \"a\\nb\\tc\\\"d\\\\e\"\n", """{"msg":"a\nb\tc\"d\\e"}"""),
        new("13_yaml_unicode_and_esc", "msg: \"\\e\\u0041\\U0001F600\"\n", """{"msg":"\u001bA\uD83D\uDE00"}"""),
        new("14_multiline_double_quoted", "notes: \"line one\n  line two\"\nbase: main\n", """{"notes":"line one line two","base":"main"}"""),
        new("15_multiline_single_quoted", "notes: 'line one\n  line two'\nbase: main\n", """{"notes":"line one line two","base":"main"}"""),
        new("16_plain_quotes_inline_comment", "title: It's a \"test\" # note\n", """{"title":"It's a \"test\""}"""),
        new("17_url_and_colon", "url: https://example.com/a:b\nlabel: \"Step 1: init\"\n", """{"url":"https://example.com/a:b","label":"Step 1: init"}"""),
        new("18_numeric_formats", "i: -42\nf: 3.5\nsci: 1e3\nhex: 0x1F\ndot: .5\n", """{"i":-42,"f":3.5,"sci":1000,"hex":31,"dot":0.5}"""),
        new("19_booleans_and_nulls", "t: true\nf: false\nn1: null\nn2: ~\nn3:\n", """{"t":true,"f":false,"n1":null,"n2":null,"n3":null}"""),
        new("20_quoted_scalars_stay_strings", "n: \"123\"\nb: 'true'\nz: \"null\"\n", """{"n":"123","b":"true","z":"null"}"""),
        new("21_all_digit_commit_hash", "source_intent_commit: 1234567\ncommits:\n  api: 0012345\n", """{"source_intent_commit":"1234567","commits":{"api":"0012345"}}""", UseStringKeys: true),
        new("22_document_markers", "---\nversion: 1\nbranch: change/demo\n...\n", """{"version":1,"branch":"change/demo"}"""),
        new("23_sequence_of_scalars", "items:\n  - alpha\n  - beta\n  - gamma\n", """{"items":["alpha","beta","gamma"]}"""),
        new("24_sequence_of_mappings", "tasks:\n  - id: T1\n    done: true\n  - id: T2\n    done: false\n", """{"tasks":[{"id":"T1","done":true},{"id":"T2","done":false}]}"""),
        new("25_sequence_of_block_scalars", "items:\n  - |-\n    first\n    second\n  - third\n", """{"items":["first\nsecond","third"]}"""),
        new("26_nested_sequences", "grid:\n  - [1, 2]\n  - - a\n    - b\n", """{"grid":[[1,2],["a","b"]]}"""),
        new("27_anchors_and_aliases", "base: &b\n  x: 1\ncopy: *b\n", """{"base":{"x":1},"copy":{"x":1}}"""),
        new("28_explicit_str_tag", "val: !!str 123\n", """{"val":"123"}"""),
        new("29_duplicate_keys_refused", "a: 1\na: 2\n", null, ExpectRefusal: true),
        new("30_comments_preserved_value_intact", "# header\nversion: 1 # inline\nscope: demo\n", """{"version":1,"scope":"demo"}"""),
        new("31_plain_multiline_continuation", "desc: hello\n    world\nbase: main\n", """{"desc":"hello world","base":"main"}"""),
        new("32_empty_collections", "arr: []\nobj: {}\n", """{"arr":[],"obj":{}}"""),
        new("33_invalid_flow_trailing_text", "title: [WIP] Fix bug\n", null, ExpectRefusal: true),
        new("34_iso_date_stays_string", "date: 2026-11-22\nreviewed_at: 2026-11-22\n", """{"date":"2026-11-22","reviewed_at":"2026-11-22"}"""),
    ];

    public static IEnumerable<object[]> SampleNames() => Samples.Select(s => new object[] { s.Name });

    [Fact]
    public void DifferentialSuite_ContainsAll34Samples()
    {
        Assert.Equal(34, Samples.Count);
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void YamlConverter_MatchesCanonicalAndOriginalYamlMjs(string sampleName)
    {
        var sample = Samples.Single(s => s.Name == sampleName);
        var converter = new YamlToJsoncConverter();
        var options = sample.UseStringKeys
            ? new YamlConversionOptions(YamlToJsoncConverter.DefaultStringKeys)
            : new YamlConversionOptions();

        if (sample.ExpectRefusal)
        {
            Assert.ThrowsAny<Exception>(() => converter.ConvertBlock(sample.Yaml, options));
            return;
        }

        var jsonc = converter.ConvertBlock(sample.Yaml, options);
        Assert.True(Jsonc.TryParse(jsonc, out var actualElem, out var err), $"{sampleName}: invalid JSONC output ({err}):\n{jsonc}");
        Assert.NotNull(actualElem);

        using var expectedDoc = JsonDocument.Parse(sample.ExpectedJson!);
        Assert.True(
            JsonElement.DeepEquals(expectedDoc.RootElement, actualElem!.Value),
            $"{sampleName}: mismatch.\nExpected: {expectedDoc.RootElement.GetRawText()}\nActual:   {actualElem.Value.GetRawText()}");

        if (File.Exists(SiblingYamlMjs) && !sample.UseStringKeys)
        {
            var jsResult = ParseWithOriginalYamlMjs(sample.Yaml);
            if (jsResult is not null)
            {
                using var jsDoc = JsonDocument.Parse(jsResult);
                Assert.True(
                    JsonElement.DeepEquals(jsDoc.RootElement, actualElem.Value),
                    $"{sampleName}: differs from yaml.mjs.\nyaml.mjs: {jsDoc.RootElement.GetRawText()}\n.NET:     {actualElem.Value.GetRawText()}");
            }
        }
    }

    private static string? ParseWithOriginalYamlMjs(string yaml)
    {
        var url = new Uri(SiblingYamlMjs).AbsoluteUri;
        var script = $"import {{ parse }} from {JsonSerializer.Serialize(url)}; const input = await new Promise(r => {{ let s=''; process.stdin.on('data', d => s += d); process.stdin.on('end', () => r(s)); }}); console.log(JSON.stringify(parse(input)));";
        var res = TestServices.CommandRunner.Run(new CommandRequest(RepoRoot, ["node", "--input-type=module", "-e", script], StandardInput: yaml));
        return res.Ok ? res.Stdout.Trim() : null;
    }
}
