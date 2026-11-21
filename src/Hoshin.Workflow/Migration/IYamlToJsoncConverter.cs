namespace Hoshin.Workflow.Migration;

/// <summary>Options controlling YAML-to-JSONC conversion.</summary>
public sealed record YamlConversionOptions(
    IReadOnlySet<string>? StringKeys = null,
    bool ForceLayout2 = false,
    bool ExpectArray = false,
    bool RootLeadingCommentsOutside = false);

/// <summary>Converts Hoshin's structured YAML blocks (front matter, record blocks, and <c>change.yml</c>) into formatted JSONC while preserving comments and key order.</summary>
public interface IYamlToJsoncConverter
{
    string ConvertBlock(string yamlText, YamlConversionOptions? options = null);

    string ConvertFrontMatter(string yamlText, bool forceLayout2 = false);

    string ConvertChangeYaml(string yamlText);
}
