using System.Text.Json;

namespace Hoshin.Workflow.Common;

/// <summary>One JSON Schema validation error.</summary>
public sealed record SchemaError(string Keyword, string Path, string Message, string? Property);

/// <summary>Validates a parsed JSON value against a named schema. Replaceable for alternative schema libraries.</summary>
public interface IJsonSchemaValidator
{
    IReadOnlyList<SchemaError> Validate(string schemaName, JsonElement value);
}
