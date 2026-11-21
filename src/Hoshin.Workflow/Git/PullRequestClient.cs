using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Git;

public static class PullRequestStates
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";
    public const string Merged = "MERGED";
    public const string Unknown = "unknown";
}

public sealed record PullRequestRef(string Url, int Number);

public sealed record OpenPullRequestInput(string Base, string Head, string Title, string Body);

public sealed record OpenPullRequestResult(PullRequestRef? PullRequest, string? Error);

/// <summary>Abstraction for GitHub pull-request operations. Default uses <c>gh</c>; replaceable with e.g. Octokit.</summary>
public interface IPullRequestClient
{
    string? HostOf(string remote);
    string? SignInProblem(string cwd, string? host);
    PullRequestRef? OpenPullRequest(string cwd, string branch);
    OpenPullRequestResult OpenNew(string cwd, OpenPullRequestInput pr);
    string? StateOf(string cwd, string url);
}

/// <summary>Default <see cref="IPullRequestClient"/> backed by the <c>gh</c> CLI (or <c>HOSHIN_GH</c> JSON argv).</summary>
public sealed class GhPullRequestClient(ICommandRunner? runner = null) : IPullRequestClient
{
    public static GhPullRequestClient Default { get; } = new();

    private static readonly Dictionary<string, string> GhEnv = new(StringComparer.Ordinal)
    {
        ["GH_PROMPT_DISABLED"] = "1",
        ["NO_COLOR"] = "1",
    };

    private static readonly Regex ScpHost = new(@"^[\w.-]+@([\w.-]+):", RegexOptions.Compiled);
    private static readonly Regex PullNumber = new(@"/pull/(\d+)", RegexOptions.Compiled);

    private readonly ICommandRunner _runner = runner ?? ProcessCommandRunner.Default;

    private IReadOnlyList<string> GhCommand()
    {
        var value = Environment.GetEnvironmentVariable("HOSHIN_GH");
        if (string.IsNullOrEmpty(value))
        {
            return ["gh"];
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var items = doc.RootElement.EnumerateArray().ToList();
                if (items.All(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Length > 0))
                {
                    return items.Select(x => x.GetString()!).ToArray();
                }
            }
        }
        catch
        {
            // Handled below.
        }

        throw new CouldNotRunException("HOSHIN_GH must be a JSON list of the gh program and its arguments, as non-empty strings");
    }

    private (bool Ok, string Stdout, string Stderr) RunGh(string cwd, IReadOnlyList<string> args)
    {
        var cmd = GhCommand();
        var argv = new List<string>(cmd.Count + args.Count);
        argv.AddRange(cmd);
        argv.AddRange(args);
        var r = _runner.Run(new CommandRequest(cwd, argv, ExtraEnvironment: GhEnv));
        if (!r.Started)
        {
            var msg = r.StartError == "ENOENT"
                ? "declaring a change done needs the GitHub command-line tool, gh, which is not installed or not on PATH"
                : $"cannot run gh: {r.StartError}";
            throw new CouldNotRunException(msg);
        }

        return (r.Ok, r.Stdout, r.Stderr);
    }

    public string? HostOf(string remote)
    {
        var scp = ScpHost.Match(remote);
        if (scp.Success)
        {
            return scp.Groups[1].Value;
        }

        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return string.IsNullOrEmpty(uri.Host) ? null : uri.Host;
        }

        return null;
    }

    public string? SignInProblem(string cwd, string? host)
    {
        var args = new List<string> { "auth", "status" };
        if (host is not null)
        {
            args.Add("--hostname");
            args.Add(host);
        }

        var (ok, _, _) = RunGh(cwd, args);
        return ok
            ? null
            : $"you are not signed in to GitHub{(host is not null ? $" on {host}" : "")} with gh: run gh auth login{(host is not null ? $" --hostname {host}" : "")}";
    }

    public PullRequestRef? OpenPullRequest(string cwd, string branch)
    {
        var (ok, stdout, stderr) = RunGh(cwd, ["pr", "list", "--head", branch, "--state", "open", "--json", "number,url", "--limit", "1"]);
        if (!ok)
        {
            throw new CouldNotRunException($"gh could not list pull requests in {cwd}: {stderr.Trim()}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdout) ? "[]" : stdout);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = doc.RootElement[0];
        return new PullRequestRef(first.GetProperty("url").GetString()!, first.GetProperty("number").GetInt32());
    }

    public OpenPullRequestResult OpenNew(string cwd, OpenPullRequestInput pr)
    {
        var (ok, stdout, stderr) = RunGh(cwd, ["pr", "create", "--base", pr.Base, "--head", pr.Head, "--title", pr.Title, "--body", pr.Body]);
        if (!ok)
        {
            return new OpenPullRequestResult(null, (string.IsNullOrEmpty(stderr) ? stdout : stderr).Trim());
        }

        var url = stdout.Trim().Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var m = PullNumber.Match(url);
        if ((url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal)) && m.Success)
        {
            return new OpenPullRequestResult(new PullRequestRef(url, int.Parse(m.Groups[1].Value)), null);
        }

        return new OpenPullRequestResult(null, $"gh printed no pull request address: {stdout.Trim()}");
    }

    public string? StateOf(string cwd, string url)
    {
        var (ok, stdout, _) = RunGh(cwd, ["pr", "view", url, "--json", "state"]);
        if (!ok)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                ? state.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
