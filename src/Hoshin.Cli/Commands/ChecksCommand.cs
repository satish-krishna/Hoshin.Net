using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class ChecksCommand(
    IWorkspaceResolver resolver,
    ChangeLocator changeLocator,
    IGitWorktrees gitWorktrees) : IHoshinCommand
{
    private readonly IWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly ChangeLocator _changeLocator = changeLocator ?? throw new ArgumentNullException(nameof(changeLocator));
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));

    public string Name => "checks";
    public string Usage => "checks --json --change <change> [--repo r] [--system s] [--master p] [--workspace p]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["change", "repo", "system", "master", "workspace"], flags: ["json"]);
        var change = o.Get("change");
        if (!o.Has("json") || change is null || o.Positional.Count > 0)
        {
            throw new UsageException();
        }

        var r = _resolver.Resolve(Directory.GetCurrentDirectory(), new ResolveOptions(o.Get("master"), o.Get("workspace")));
        if (r.Master is null || r.Workspace is null)
        {
            throw new CouldNotRunException(string.Join('\n', r.Problems));
        }

        var found = _changeLocator.FindChangeLedger(r.Master, r.Workspace, change, o.Get("system"));
        var file = CheckLog.ChecksFile(_gitWorktrees, found.Master, found.System, change);
        var result = CheckReader.Read(file, o.Get("repo"));
        CliIo.PrintJson(new { system = found.System, change, runs = result.Runs, problems = result.Problems });
        return Task.FromResult(result.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
