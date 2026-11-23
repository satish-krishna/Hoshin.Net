using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Hoshin.Workflow.Migration;

internal static partial class YamlScalarFormatter
{
    private const string StrTag = "tag:yaml.org,2002:str";

    public static readonly JsonSerializerOptions StringJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex DecimalIntRegex();

    [GeneratedRegex(@"^0x[0-9a-fA-F]+$")]
    private static partial Regex HexIntRegex();

    [GeneratedRegex(@"^0o[0-7]+$")]
    private static partial Regex OctalIntRegex();

    [GeneratedRegex(@"^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();

    public static string FormatScalar(YamlScalarNode node, bool forceString)
    {
        var raw = node.Value ?? "";
        var hasStrTag = !node.Tag.IsEmpty && !node.Tag.IsNonSpecific && string.Equals(node.Tag.Value, StrTag, StringComparison.Ordinal);
        if (forceString || node.Style != ScalarStyle.Plain || hasStrTag)
        {
            return JsonSerializer.Serialize(raw, StringJsonOptions);
        }

        return FormatPlainScalar(raw);
    }

    private static string FormatPlainScalar(string raw)
    {
        if (raw.Length == 0 || raw is "null" or "Null" or "NULL" or "~")
        {
            return "null";
        }

        if (raw is "true" or "True" or "TRUE")
        {
            return "true";
        }

        if (raw is "false" or "False" or "FALSE")
        {
            return "false";
        }

        if (TryFormatNumber(raw, out var numeric))
        {
            return numeric;
        }

        return JsonSerializer.Serialize(raw, StringJsonOptions);
    }

    private static bool TryFormatNumber(string raw, out string formatted)
    {
        if (HexIntRegex().IsMatch(raw))
        {
            formatted = Convert.ToInt64(raw[2..], 16).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (OctalIntRegex().IsMatch(raw))
        {
            formatted = Convert.ToInt64(raw[2..], 8).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (DecimalIntRegex().IsMatch(raw) && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var intVal))
        {
            formatted = intVal.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if ((raw.Contains('.') || raw.Contains('e') || raw.Contains('E')) &&
            FloatRegex().IsMatch(raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dblVal) &&
            double.IsFinite(dblVal))
        {
            formatted = !raw.Contains('.') && dblVal == Math.Truncate(dblVal) && Math.Abs(dblVal) < 1e15
                ? ((long)dblVal).ToString(CultureInfo.InvariantCulture)
                : JsonSerializer.Serialize(dblVal);
            return true;
        }

        formatted = "";
        return false;
    }
}
