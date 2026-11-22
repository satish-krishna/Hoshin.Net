using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hoshin.Workflow.Common;

/// <summary>
/// Lightweight, zero-dependency, AOT-compatible JSON Schema draft-07 validator covering the record schema keywords
/// (<c>type</c>, <c>enum</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties</c>, <c>items</c>,
/// <c>pattern</c>, <c>minLength</c>, <c>minItems</c>, <c>minimum</c>) with GRAMMAR.md / ajv-compatible error messages.
/// </summary>
public sealed class EmbeddedSchemaValidator : IJsonSchemaValidator
{
    public static EmbeddedSchemaValidator Default { get; } = new();

    private readonly ConcurrentDictionary<string, JsonElement> _cache = new(StringComparer.Ordinal);

    public IReadOnlyList<SchemaError> Validate(string schemaName, JsonElement value)
    {
        var root = _cache.GetOrAdd(schemaName, LoadEmbedded);
        var errors = new List<SchemaError>();
        Check(root, value, "", errors);
        return errors;
    }

    /// <summary>Formats schema errors in GRAMMAR.md / doc.ts wording.</summary>
    public static IEnumerable<string> FormatMessages(IEnumerable<SchemaError> errors, string who, bool plural) =>
        errors.Select(e => e.Keyword switch
        {
            "required" => $"{who} {(plural ? "lack" : "lacks")} {e.Property}",
            "additionalProperties" => $"{who} {(plural ? "have" : "has")} unknown field {e.Property}",
            _ => $"{who} field {(e.Path.Length == 0 ? "value" : e.Path)} {e.Message}",
        });

    public static bool IsMissingRequired(SchemaError error, string property) =>
        error.Keyword == "required" && error.Property == property;

    private static JsonElement LoadEmbedded(string name)
    {
        var resource = $"Hoshin.Workflow.Documents.Schemas.{name}";
        using var stream = typeof(EmbeddedSchemaValidator).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded schema '{resource}' was not found.");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private static void Check(JsonElement schema, JsonElement value, string path, List<SchemaError> errors)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                var expected = type.GetString()!;
                if (!IsType(value, expected))
                {
                    errors.Add(new SchemaError("type", path, $"must be {expected}", null));
                    return;
                }
            }
            else if (type.ValueKind == JsonValueKind.Array)
            {
                var allowed = type.EnumerateArray().Select(t => t.GetString()!).ToList();
                if (!allowed.Any(t => IsType(value, t)))
                {
                    errors.Add(new SchemaError("type", path, $"must be {string.Join(',', allowed)}", null));
                    return;
                }
            }
        }

        if (schema.TryGetProperty("enum", out var enumProp) && enumProp.ValueKind == JsonValueKind.Array)
        {
            var matched = false;
            foreach (var candidate in enumProp.EnumerateArray())
            {
                if (JsonElementsEqual(candidate, value))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                errors.Add(new SchemaError("enum", path, "must be equal to one of the allowed values", null));
            }
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (schema.TryGetProperty("minimum", out var min) && value.GetDouble() < min.GetDouble())
                {
                    errors.Add(new SchemaError("minimum", path, $"must be >= {min.GetRawText()}", null));
                }

                break;

            case JsonValueKind.String:
                var s = value.GetString()!;
                if (schema.TryGetProperty("minLength", out var minLength) && new StringInfo(s).LengthInTextElements < minLength.GetInt32())
                {
                    errors.Add(new SchemaError("minLength", path, $"must NOT have fewer than {minLength.GetInt32()} characters", null));
                }

                if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(s, pattern.GetString()!))
                {
                    errors.Add(new SchemaError("pattern", path, $"must match pattern \"{pattern.GetString()}\"", null));
                }

                break;

            case JsonValueKind.Array:
                if (schema.TryGetProperty("minItems", out var minItems) && value.GetArrayLength() < minItems.GetInt32())
                {
                    errors.Add(new SchemaError("minItems", path, $"must NOT have fewer than {minItems.GetInt32()} items", null));
                }

                if (schema.TryGetProperty("items", out var items))
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        Check(items, item, Join(path, i++.ToString(CultureInfo.InvariantCulture)), errors);
                    }
                }

                break;

            case JsonValueKind.Object:
                if (schema.TryGetProperty("required", out var required))
                {
                    foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                    {
                        if (!value.TryGetProperty(name, out _))
                        {
                            errors.Add(new SchemaError("required", path, $"must have required property '{name}'", name));
                        }
                    }
                }

                var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
                if (schema.TryGetProperty("additionalProperties", out var additional))
                {
                    if (additional.ValueKind == JsonValueKind.False)
                    {
                        foreach (var prop in value.EnumerateObject())
                        {
                            if (!hasProperties || !properties.TryGetProperty(prop.Name, out _))
                            {
                                errors.Add(new SchemaError("additionalProperties", path, "must NOT have additional properties", prop.Name));
                            }
                        }
                    }
                    else if (additional.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in value.EnumerateObject())
                        {
                            if (!hasProperties || !properties.TryGetProperty(prop.Name, out _))
                            {
                                Check(additional, prop.Value, Join(path, prop.Name), errors);
                            }
                        }
                    }
                }

                if (hasProperties)
                {
                    foreach (var prop in properties.EnumerateObject())
                    {
                        if (value.TryGetProperty(prop.Name, out var child))
                        {
                            Check(prop.Value, child, Join(path, prop.Name), errors);
                        }
                    }
                }

                break;
        }
    }

    private static string Join(string path, string part) => path.Length == 0 ? part : $"{path}.{part}";

    private static bool IsType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
        _ => true,
    };

    private static bool IsInteger(JsonElement value)
    {
        if (value.TryGetInt64(out _))
        {
            return true;
        }

        if (value.TryGetDouble(out var d))
        {
            return Math.Truncate(d) == d && !double.IsInfinity(d) && !double.IsNaN(d);
        }

        return false;
    }

    private static bool JsonElementsEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetDouble() == b.GetDouble(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }
}
