using Hoshin.Workflow.Hosting;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Hoshin.Tests.Architecture;

public sealed record SourceFile(string RelativePath, string Text);

public sealed record ArchitectureViolation(string Rule, string Key, string Detail)
{
    public string FullKey => $"{Rule}:{Key}";
}

/// <summary>
/// Evaluates the architecture and SOLID design rules from CONTRIBUTING.md §3–§5 against compiled assemblies and source files.
/// </summary>
public static partial class ArchitectureRules
{
    private static readonly HashSet<string> AllowedWorkflowRefPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Microsoft.Win32",
        "netstandard",
        "mscorlib",
        "Markdig",
        "Gherkin",
        "Cucumber.Messages",
    };

    private static readonly string[] HarnessTokens =
    [
        "hook_event_name",
        "hookEventName",
        "toolCall",
        "invoke_subagent",
        "UserPromptExpansion",
        "PreInvocation",
        "SubagentStop",
        "additionalContext",
        "injectSteps",
        "permissionDecision",
    ];

    private static readonly string[] PureEngineFiles =
    [
        "src/Hoshin.Workflow/Engine/WorkflowEngine.cs",
        "src/Hoshin.Workflow/Engine/RedClassifier.cs",
    ];

    [GeneratedRegex(@"\b(System\.IO|System\.Diagnostics\.Process|Environment\.|DateTime\.Now\b|DateTime\.UtcNow\b|DateTimeOffset\.Now\b|DateTimeOffset\.UtcNow\b|File\.|Directory\.)")]
    private static partial Regex PureIoRegex();

    [GeneratedRegex(@"\bDateTime(?:Offset)?\.(?:Utc)?Now\b")]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"\?\?\s*(?:new\s+([A-Za-z_][A-Za-z0-9_<>.]*)\s*\(|([A-Za-z_][A-Za-z0-9_.]+)\.Default\b)")]
    private static partial Regex FallbackCoalesceRegex();

    [GeneratedRegex(@"SuppressMessage\s*\(\s*""Maintainability""\s*,\s*""CA1502")]
    private static partial Regex Ca1502SuppressionRegex();

    [GeneratedRegex(@"\bcatch\b(?:\s*\(([^)]*)\))?\s*\{")]
    private static partial Regex CatchHeaderRegex();

    [GeneratedRegex(@"\b(throw\b|WriteStderr|AppendTrace|Log\w*\s*\(|Trace\w*\s*\()")]
    private static partial Regex CatchHandlerSignalRegex();

    public static List<ArchitectureViolation> EvaluateAll(
        Assembly workflowAssembly,
        Assembly cliAssembly,
        IReadOnlyList<SourceFile> sourceFiles,
        IReadOnlyDictionary<string, string>? defaultPropertyAllowList = null)
    {
        var results = new List<ArchitectureViolation>();
        results.AddRange(CheckA1(workflowAssembly));
        results.AddRange(CheckA2(sourceFiles));
        results.AddRange(CheckS2(sourceFiles));
        results.AddRange(CheckS3FileLength(sourceFiles));
        results.AddRange(CheckS3Ca1502(sourceFiles));
        results.AddRange(CheckI1(workflowAssembly, cliAssembly));
        results.AddRange(CheckI2(cliAssembly));
        results.AddRange(CheckD1(sourceFiles));
        results.AddRange(CheckD2(workflowAssembly, cliAssembly));
        results.AddRange(CheckD3Fallbacks(sourceFiles));
        results.AddRange(CheckD3DefaultProperties([workflowAssembly, cliAssembly], defaultPropertyAllowList));
        results.AddRange(CheckD4(workflowAssembly, cliAssembly));
        results.AddRange(CheckC3(sourceFiles));
        return results;
    }

    public static List<ArchitectureViolation> CheckA1(Assembly workflowAssembly)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var reference in workflowAssembly.GetReferencedAssemblies())
        {
            var name = reference.Name ?? "";
            if (name.Equals("Hoshin.Cli", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("hoshin", StringComparison.OrdinalIgnoreCase) ||
                !AllowedWorkflowRefPrefixes.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase) || name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(new ArchitectureViolation("A1", name, $"Hoshin.Workflow references disallowed assembly '{name}' (CONTRIBUTING.md §5.1)."));
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckA2(IReadOnlyList<SourceFile> files)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var file in files)
        {
            var rel = NormalizePath(file.RelativePath);
            if (rel.StartsWith("src/Hoshin.Cli/Hooks/", StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("src/Hoshin.Cli/Commands/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A2 scans raw source (strings included, comments stripped) because payload names appear in string literals.
            var withoutComments = StripCommentsOnly(file.Text);
            foreach (var token in HarnessTokens)
            {
                if (withoutComments.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add(new ArchitectureViolation("A2", $"{rel}#{token}", $"{rel} mentions harness payload token '{token}' outside Hoshin.Cli/Hooks or Commands (CONTRIBUTING.md §5.2)."));
                }
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckS2(IReadOnlyList<SourceFile> files)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var file in files)
        {
            var rel = NormalizePath(file.RelativePath);
            if (!PureEngineFiles.Contains(rel, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = StripCommentsAndStrings(file.Text);
            foreach (Match m in PureIoRegex().Matches(code))
            {
                var token = m.Groups[1].Value;
                var key = $"{rel}#{token}";
                if (!violations.Any(v => v.Key == key))
                {
                    violations.Add(new ArchitectureViolation("S2", key, $"{rel} uses impure API '{token}' (CONTRIBUTING.md §3.2 S2)."));
                }
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckS3FileLength(IReadOnlyList<SourceFile> files, int maxLines = 400)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var file in files)
        {
            var lineCount = file.Text.Split('\n').Length;
            if (lineCount > maxLines)
            {
                var rel = NormalizePath(file.RelativePath);
                violations.Add(new ArchitectureViolation("S3", rel, $"{rel} has {lineCount} lines (limit {maxLines}; CONTRIBUTING.md §3.2 S3)."));
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckS3Ca1502(IReadOnlyList<SourceFile> files)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var file in files)
        {
            var rel = NormalizePath(file.RelativePath);
            var lines = file.Text.Replace("\r\n", "\n").Split('\n');
            var seenInFile = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Ca1502SuppressionRegex().IsMatch(lines[i]))
                {
                    continue;
                }

                var methodName = FindNextMethodName(lines, i + 1);
                var count = seenInFile.GetValueOrDefault(methodName) + 1;
                seenInFile[methodName] = count;
                var key = count == 1 ? $"{rel}#{methodName}" : $"{rel}#{methodName}#{count}";
                violations.Add(new ArchitectureViolation("CA1502", key, $"{rel}: baselined CA1502 complexity suppression on {methodName} (CONTRIBUTING.md §3.2 S3)."));
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckI1(params Assembly[] assemblies)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var asm in assemblies)
        {
            foreach (var type in GetLoadableTypes(asm))
            {
                if (!type.IsInterface || IsCompilerGenerated(type))
                {
                    continue;
                }

                var members = CountInterfaceMembers(type);
                if (members > 10)
                {
                    violations.Add(new ArchitectureViolation("I1", type.FullName ?? type.Name, $"Interface {type.FullName} has {members} members (limit 10; CONTRIBUTING.md §3.5 I1)."));
                }
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckI1ForTypes(IEnumerable<Type> types)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var type in types)
        {
            if (!type.IsInterface || IsCompilerGenerated(type))
            {
                continue;
            }

            var members = CountInterfaceMembers(type);
            if (members > 10)
            {
                violations.Add(new ArchitectureViolation("I1", type.FullName ?? type.Name, $"Interface {type.FullName} has {members} members (limit 10; CONTRIBUTING.md §3.5 I1)."));
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckI2(Assembly cliAssembly) =>
        CheckI2ForTypes(GetLoadableTypes(cliAssembly).Where(t => t.Namespace == "Hoshin.Cli.Commands" && !t.IsInterface));

    public static List<ArchitectureViolation> CheckI2ForTypes(IEnumerable<Type> commandTypes)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var type in commandTypes)
        {
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var param in ctor.GetParameters())
                {
                    if (param.ParameterType.Name == "HoshinServices")
                    {
                        violations.Add(new ArchitectureViolation("I2", $"{type.FullName}..ctor({param.Name})", $"Command {type.FullName} takes HoshinServices (CONTRIBUTING.md §3.5 I2)."));
                    }
                }
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckD1(IReadOnlyList<SourceFile> files)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var file in files)
        {
            var rel = NormalizePath(file.RelativePath);
            if (rel.EndsWith("Clock.cs", StringComparison.OrdinalIgnoreCase) ||
                rel.EndsWith("SystemClock.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = StripCommentsAndStrings(file.Text);
            var matches = ClockRegex().Matches(code).Select(m => m.Value).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
            foreach (var match in matches)
            {
                violations.Add(new ArchitectureViolation("D1", $"{rel}#{match}", $"{rel} calls {match} directly instead of TimeProvider (CONTRIBUTING.md §3.6 D1)."));
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckD2(params Assembly[] assemblies) =>
        CheckD2ForTypes(assemblies.SelectMany(GetLoadableTypes));

    public static List<ArchitectureViolation> CheckD2ForTypes(IEnumerable<Type> types)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var type in types)
        {
            if (IsCompilerGenerated(type) || type.IsInterface || type.IsEnum)
            {
                continue;
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!ctor.IsPublic && !ctor.IsAssembly)
                {
                    continue;
                }

                foreach (var param in ctor.GetParameters())
                {
                    if (!param.HasDefaultValue)
                    {
                        continue;
                    }

                    var pt = param.ParameterType;
                    var isDependency = pt.IsInterface ||
                        (pt.IsClass && (pt.Namespace?.StartsWith("Hoshin.", StringComparison.Ordinal) ?? false) && !IsDataRecord(pt));
                    if (isDependency)
                    {
                        violations.Add(new ArchitectureViolation("D2", $"{type.FullName}..ctor({param.Name})", $"{type.FullName} constructor parameter '{param.Name}' ({pt.Name}) has a default value (CONTRIBUTING.md §3.6 D2)."));
                    }
                }
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckD3Fallbacks(IReadOnlyList<SourceFile> files)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var file in files)
        {
            var rel = NormalizePath(file.RelativePath);
            if (rel.EndsWith("/HoshinServices.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = StripCommentsAndStrings(file.Text);
            foreach (Match m in FallbackCoalesceRegex().Matches(code))
            {
                var target = m.Groups[1].Success ? $"new {m.Groups[1].Value}" : $"{m.Groups[2].Value}.Default";
                var key = $"{rel}#{target}";
                if (!violations.Any(v => v.Key == key))
                {
                    violations.Add(new ArchitectureViolation("D3", key, $"{rel} uses fallback '?? {target}' outside HoshinServices.cs (CONTRIBUTING.md §3.6 D3)."));
                }
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckD3DefaultProperties(
        IEnumerable<Assembly> assemblies,
        IReadOnlyDictionary<string, string>? allowList = null) =>
        CheckD3DefaultPropertiesForTypes(assemblies.SelectMany(GetLoadableTypes), allowList);

    public static List<ArchitectureViolation> CheckD3DefaultPropertiesForTypes(
        IEnumerable<Type> types,
        IReadOnlyDictionary<string, string>? allowList = null)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var type in types)
        {
            if (IsCompilerGenerated(type) || type.IsInterface)
            {
                continue;
            }

            var prop = type.GetProperty("Default", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (prop is null)
            {
                continue;
            }

            // JsonSerializerContext generated Default property is required by System.Text.Json source generation.
            if (typeof(System.Text.Json.Serialization.JsonSerializerContext).IsAssignableFrom(type))
            {
                continue;
            }

            var key = $"{type.FullName}.Default";
            if (allowList is not null && allowList.ContainsKey(key))
            {
                continue;
            }

            violations.Add(new ArchitectureViolation("D3", key, $"{key} is an unlisted static Default singleton (CONTRIBUTING.md §3.6 D3)."));
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckD4(params Assembly[] assemblies) =>
        CheckD4ForTypes(assemblies.SelectMany(GetLoadableTypes));

    public static List<ArchitectureViolation> CheckD4ForTypes(IEnumerable<Type> types)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var type in types)
        {
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsCompilerGenerated(field) || field.IsInitOnly || field.IsLiteral)
                {
                    continue;
                }

                violations.Add(new ArchitectureViolation("D4", $"{type.FullName}.{field.Name}", $"Mutable static field {type.FullName}.{field.Name} (CONTRIBUTING.md §3.6 D4)."));
            }
        }

        return violations;
    }

    public static List<ArchitectureViolation> CheckC3(IReadOnlyList<SourceFile> files)
    {
        var violations = new List<ArchitectureViolation>();
        foreach (var file in files)
        {
            var rel = NormalizePath(file.RelativePath);
            var code = StripCommentsAndStrings(file.Text);
            var count = 0;
            foreach (Match m in CatchHeaderRegex().Matches(code))
            {
                var clause = m.Groups[1].Value.Trim();
                var isBroad = string.IsNullOrEmpty(clause) ||
                    clause.StartsWith("Exception", StringComparison.Ordinal) ||
                    clause.StartsWith("System.Exception", StringComparison.Ordinal);
                if (!isBroad)
                {
                    continue;
                }

                var body = ExtractBraceBody(code, m.Index + m.Length - 1);
                if (!CatchHandlerSignalRegex().IsMatch(body))
                {
                    count++;
                    var line = 1 + code[..m.Index].Count(c => c == '\n');
                    violations.Add(new ArchitectureViolation("C3", $"{rel}#catch-{count}", $"{rel}:{line} has a silent catch block that neither throws nor logs (CONTRIBUTING.md §4 C3)."));
                }
            }
        }

        return violations;
    }

    private static int CountInterfaceMembers(Type iface)
    {
        var methods = iface.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(m => !m.IsSpecialName);
        var props = iface.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length;
        var events = iface.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length;
        return methods + props + events;
    }

    private static bool IsDataRecord(Type t)
    {
        // Records in C# have a compiler-synthesized <Clone>$ method and typically hold plain data, not services.
        var isRecord = t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;
        if (!isRecord)
        {
            return false;
        }

        // Data records do not implement service interfaces or end with service suffixes.
        return !t.Name.EndsWith("Service", StringComparison.Ordinal)
            && !t.Name.EndsWith("Runner", StringComparison.Ordinal)
            && !t.Name.EndsWith("Client", StringComparison.Ordinal)
            && !t.Name.EndsWith("Reader", StringComparison.Ordinal)
            && !t.Name.EndsWith("Store", StringComparison.Ordinal);
    }

    private static string FindNextMethodName(string[] lines, int start)
    {
        for (var i = start; i < Math.Min(lines.Length, start + 6); i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('[') || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var paren = trimmed.IndexOf('(');
            if (paren > 0)
            {
                var before = trimmed[..paren].Trim();
                var lastSpace = before.LastIndexOf(' ');
                return lastSpace >= 0 ? before[(lastSpace + 1)..] : before;
            }
        }

        return $"line-{start + 1}";
    }

    private static string ExtractBraceBody(string code, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return code[(openBraceIndex + 1)..i];
                }
            }
        }

        return code[(openBraceIndex + 1)..];
    }

    public static string StripCommentsOnly(string source) => StripCore(source, stripStrings: false);

    public static string StripCommentsAndStrings(string source) => StripCore(source, stripStrings: true);

    private static string StripCore(string source, bool stripStrings)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n')
                    {
                        sb.Append('\n');
                    }

                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                continue;
            }

            if (stripStrings && c == '"')
            {
                // Raw string literal (""" ... """)
                if (i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
                {
                    var quotes = 0;
                    while (i + quotes < source.Length && source[i + quotes] == '"')
                    {
                        quotes++;
                    }

                    i += quotes;
                    sb.Append("\"\"");
                    var run = 0;
                    while (i < source.Length)
                    {
                        if (source[i] == '\n')
                        {
                            sb.Append('\n');
                        }

                        if (source[i] == '"')
                        {
                            run++;
                            i++;
                            if (run == quotes)
                            {
                                break;
                            }
                        }
                        else
                        {
                            run = 0;
                            i++;
                        }
                    }

                    continue;
                }

                var verbatim = i > 0 && (source[i - 1] == '@' || (i > 1 && source[i - 2] == '@' && source[i - 1] == '$'));
                sb.Append("\"\"");
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\n')
                    {
                        sb.Append('\n');
                    }

                    if (verbatim)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < source.Length && source[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        i++;
                    }
                    else
                    {
                        if (source[i] == '\\' && i + 1 < source.Length)
                        {
                            i += 2;
                            continue;
                        }

                        if (source[i] == '"')
                        {
                            i++;
                            break;
                        }

                        i++;
                    }
                }

                continue;
            }

            if (stripStrings && c == '\'')
            {
                sb.Append("''");
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        i += 2;
                        continue;
                    }

                    if (source[i] == '\'')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private static bool IsCompilerGenerated(MemberInfo member) =>
        member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) ||
        member.Name.Contains('<', StringComparison.Ordinal);

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
