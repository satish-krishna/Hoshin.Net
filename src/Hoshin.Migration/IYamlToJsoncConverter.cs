namespace Hoshin.Workflow.Migration;

/// <summary>Options controlling YAML-to-JSONC conversion.</summary>
public sealed record YamlConversionOptions(
    IReadOnlySet<string>? StringKeys = null,
    bool ForceLayout2 = false,
    bool ExpectArray = false,
    bool RootLeadingCommentsOutside = false);

/// <summary>Structured result of YAML-to-JSONC conversion including any non-fatal comment warnings.</summary>
public sealed record YamlConversionResult(string Jsonc, IReadOnlyList<string> Warnings);

/// <summary>Converts Hoshin's structured YAML blocks (front matter, record blocks, and <c>change.yml</c>) into formatted JSONC while preserving key order and comments.</summary>
public interface IYamlToJsoncConverter
{
    string ConvertBlock(string yamlText, YamlConversionOptions? options = null);

    YamlConversionResult ConvertBlockWithWarnings(string yamlText, YamlConversionOptions? options = null);

    string ConvertFrontMatter(string yamlText, bool forceLayout2 = false);

    string ConvertChangeYaml(string yamlText);
}
