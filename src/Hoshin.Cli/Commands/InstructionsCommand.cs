using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class InstructionsCommand(IWorkspaceResolver resolver, MarkerReader markerReader) : IHoshinCommand
{
    private readonly IWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly MarkerReader _markerReader = markerReader ?? throw new ArgumentNullException(nameof(markerReader));

    public string Name => "instructions";
    public string Usage => "instructions --json [--write] [--allow-home] [--master <path>] [--workspace <path>]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["master", "workspace"], flags: ["json", "write", "allow-home"]);
        if (!o.Has("json") || o.Positional.Count > 0)
        {
            throw new UsageException();
        }

        var r = _resolver.Resolve(Directory.GetCurrentDirectory(), new ResolveOptions(o.Get("master"), o.Get("workspace")));
        if (r.Master is null || r.Workspace is null)
        {
            throw new CouldNotRunException(string.Join('\n', r.Problems));
        }

        var result = InstructionsService.Run(new InstructionsInput(
            r.Workspace,
            r.Master,
            _resolver.MasterFolderName(r.Workspace, r.Master),
            _markerReader.ReadMarker(Path.Combine(r.Master, MarkerConstants.FileName)),
            o.Has("write"),
            o.Has("allow-home")));

        CliIo.PrintJson(result);
        return Task.FromResult(result.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
