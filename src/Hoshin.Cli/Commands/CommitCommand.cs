using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class CommitCommand(Committer committer) : IHoshinCommand
{
    private readonly Committer _committer = committer ?? throw new ArgumentNullException(nameof(committer));

    public string Name => "commit";
    public string Usage => "commit --json --kind <kind> [--system <s>] [--change <c>] [--repo <repo> [--task T<n>] --type <type> --subject \"<text>\"] [--co-author \"<name> <email>\"] [--master <path>] [--workspace <path>] -- <path>...";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var split = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--")
            {
                split = i;
                break;
            }
        }

        if (split == -1)
        {
            throw new UsageException();
        }

        var o = OptionParser.Parse(
            args.Take(split).ToList(),
            values: ["kind", "system", "change", "repo", "task", "type", "subject", "co-author", "master", "workspace"],
            flags: ["json"]);
        var paths = args.Skip(split + 1).ToList();
        var kind = o.Get("kind");
        if (!o.Has("json") || o.Positional.Count > 0 || kind is null || paths.Count == 0)
        {
            throw new UsageException();
        }

        var outcome = _committer.Commit(new CommitRequest(
            kind,
            o.Get("system"),
            o.Get("change"),
            o.Get("repo"),
            o.Get("task"),
            o.Get("type"),
            o.Get("subject"),
            o.Get("co-author"),
            new ResolveOptions(o.Get("master"), o.Get("workspace")),
            paths,
            Directory.GetCurrentDirectory()));

        CliIo.PrintJson(outcome);
        return Task.FromResult(outcome.Reason is "check failed" or "git refused" ? ExitCodes.Problems : ExitCodes.Pass);
    }
}
