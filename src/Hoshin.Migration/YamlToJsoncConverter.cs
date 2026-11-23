using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Hoshin.Workflow.Migration;

/// <summary>
/// Converts YAML blocks into formatted JSONC using YamlDotNet's representation model.
/// Preserves mapping key order, attaches comments exposed by YamlDotNet's scanner (recording a warning
/// if any comment could not be preserved), quotes all-digit commit hashes, and refuses invalid YAML
/// or duplicate mapping keys rather than dropping data.
/// </summary>
public sealed class YamlToJsoncConverter : IYamlToJsoncConverter
{
    public static readonly IReadOnlySet<string> DefaultStringKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "source_intent_commit",
        "source_spec_commit",
        "source_commit",
        "commits",
        "pushed",
        "commit",
        "red",
        "base",
    };

    private static readonly YamlConversionOptions DefaultOptions = new(DefaultStringKeys);

    public string ConvertBlock(string yamlText, YamlConversionOptions? options = null) =>
        ConvertBlockWithWarnings(yamlText, options).Jsonc;

    public string ConvertFrontMatter(string yamlText, bool forceLayout2 = false) =>
        ConvertBlock(yamlText, new YamlConversionOptions(DefaultStringKeys, forceLayout2, ExpectArray: false));

    public string ConvertChangeYaml(string yamlText) =>
        ConvertBlock(yamlText, new YamlConversionOptions(DefaultStringKeys, ForceLayout2: false, ExpectArray: false, RootLeadingCommentsOutside: true));

    public YamlConversionResult ConvertBlockWithWarnings(string yamlText, YamlConversionOptions? options = null)
    {
        var opts = options is null ? DefaultOptions : options;
        var normalized = yamlText.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Length > 0 && !normalized.EndsWith('\n'))
        {
            normalized += "\n";
        }
        var comments = ScanCommentsAndValidateUniqueKeys(normalized);
        var root = LoadSingleRootNode(normalized);

        if (root is null)
        {
            return new YamlConversionResult(YamlJsoncEmitter.EmitEmpty(comments, opts), []);
        }

        if (opts.ExpectArray && root is not YamlSequenceNode)
        {
            throw new FormatException($"YAML block expected a sequence, found {root.NodeType}");
        }

        var emitter = new YamlJsoncEmitter(comments, opts);
        var jsonc = emitter.EmitRoot(root);
        return new YamlConversionResult(jsonc, emitter.Warnings);
    }

    private static List<ScannedYamlComment> ScanCommentsAndValidateUniqueKeys(string yaml)
    {
        var comments = new List<ScannedYamlComment>();
        var mapKeys = new Stack<(bool ExpectingKey, HashSet<string> Keys)>();
        var containerKinds = new Stack<bool>();

        try
        {
            var parser = new Parser(new Scanner(new StringReader(yaml), skipComments: false));
            while (parser.MoveNext())
            {
                ProcessScanEvent(parser.Current, comments, mapKeys, containerKinds);
            }
        }
        catch (YamlException e)
        {
            throw new FormatException($"YAML line {e.Start.Line}: {e.Message}", e);
        }

        return comments;
    }

    private static void ProcessScanEvent(
        ParsingEvent? ev,
        List<ScannedYamlComment> comments,
        Stack<(bool ExpectingKey, HashSet<string> Keys)> mapKeys,
        Stack<bool> containerKinds)
    {
        switch (ev)
        {
            case Comment c:
                comments.Add(new ScannedYamlComment(c.Start.Line, c.Value.TrimEnd(), c.IsInline));
                return;
            case MappingStart:
                MarkValueOrKeyConsumed(mapKeys, containerKinds, keyText: null, isContainerStart: true);
                containerKinds.Push(true);
                mapKeys.Push((true, new HashSet<string>(StringComparer.Ordinal)));
                return;
            case MappingEnd:
                if (containerKinds.Count > 0)
                {
                    containerKinds.Pop();
                }

                if (mapKeys.Count > 0)
                {
                    mapKeys.Pop();
                }

                return;
            case SequenceStart:
                MarkValueOrKeyConsumed(mapKeys, containerKinds, keyText: null, isContainerStart: true);
                containerKinds.Push(false);
                return;
            case SequenceEnd:
                if (containerKinds.Count > 0)
                {
                    containerKinds.Pop();
                }

                return;
            case Scalar s:
                MarkValueOrKeyConsumed(mapKeys, containerKinds, s.Value, isContainerStart: false);
                return;
            case AnchorAlias:
                MarkValueOrKeyConsumed(mapKeys, containerKinds, keyText: null, isContainerStart: false);
                return;
        }
    }

    private static void MarkValueOrKeyConsumed(
        Stack<(bool ExpectingKey, HashSet<string> Keys)> mapKeys,
        Stack<bool> containerKinds,
        string? keyText,
        bool isContainerStart)
    {
        if (containerKinds.Count == 0 || !containerKinds.Peek() || mapKeys.Count == 0)
        {
            return;
        }

        var top = mapKeys.Pop();
        if (top.ExpectingKey)
        {
            if (!isContainerStart && keyText is not null && !top.Keys.Add(keyText))
            {
                throw new FormatException($"YAML duplicate key '{keyText}'");
            }

            mapKeys.Push((false, top.Keys));
        }
        else
        {
            mapKeys.Push((true, top.Keys));
        }
    }

    private static YamlNode? LoadSingleRootNode(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException e)
        {
            throw new FormatException($"YAML line {e.Start.Line}: {e.Message}", e);
        }
        catch (ArgumentException e)
        {
            throw new FormatException($"YAML duplicate key: {e.Message}", e);
        }

        if (stream.Documents.Count == 0)
        {
            return null;
        }

        if (stream.Documents.Count > 1)
        {
            throw new FormatException("YAML block contains multiple documents");
        }

        var root = stream.Documents[0].RootNode;
        if (root is YamlScalarNode s && s.Style == ScalarStyle.Plain && string.IsNullOrEmpty(s.Value))
        {
            return null;
        }

        return root;
    }
}
