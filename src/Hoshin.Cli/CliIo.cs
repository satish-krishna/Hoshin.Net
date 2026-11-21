using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hoshin.Cli;

/// <summary>Raw UTF-8 stdin/stdout/stderr helpers preventing Windows OEM codepage corruption.</summary>
public static class CliIo
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions IgnoreNullJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ReadStdinUtf8()
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static void WriteStdoutLine(string text)
    {
        var bytes = Utf8NoBom.GetBytes(text + "\n");
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }

    public static void WriteStderrLine(string text)
    {
        var bytes = Utf8NoBom.GetBytes(text + "\n");
        using var stderr = Console.OpenStandardError();
        stderr.Write(bytes, 0, bytes.Length);
        stderr.Flush();
    }

    public static void PrintJson<T>(T value) =>
        WriteStdoutLine(JsonSerializer.Serialize(value, JsonOptions));

    public static void PrintRawJson(string json) =>
        WriteStdoutLine(json);
}
