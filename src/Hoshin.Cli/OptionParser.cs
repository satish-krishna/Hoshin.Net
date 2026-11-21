namespace Hoshin.Cli;

public sealed class UsageException : Exception;

public sealed record ParsedOptions(
    List<string> Positional,
    Dictionary<string, string> Values,
    HashSet<string> Flags)
{
    public string? Get(string name) => Values.GetValueOrDefault(name);
    public bool Has(string flag) => Flags.Contains(flag);
}

/// <summary>Parses <c>--name value</c> options and <c>--flag</c> switches, refusing unknown or repeated options.</summary>
public static class OptionParser
{
    public static ParsedOptions Parse(IReadOnlyList<string> args, IReadOnlyList<string>? values = null, IReadOnlyList<string>? flags = null)
    {
        var valueSet = values is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(values, StringComparer.Ordinal);
        var flagSet = flags is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(flags, StringComparer.Ordinal);

        var positional = new List<string>();
        var parsedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsedFlags = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (flagSet.Contains(name))
            {
                if (!parsedFlags.Add(name))
                {
                    throw new UsageException();
                }
            }
            else if (valueSet.Contains(name))
            {
                if (i + 1 >= args.Count || parsedValues.ContainsKey(name))
                {
                    throw new UsageException();
                }

                parsedValues[name] = args[++i];
            }
            else
            {
                throw new UsageException();
            }
        }

        return new ParsedOptions(positional, parsedValues, parsedFlags);
    }
}
