using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;

namespace Hoshin.Cli.Commands;

public sealed class FixStateCommand(FixStateService fixState) : IHoshinCommand
{
    private readonly FixStateService _fixState = fixState ?? throw new ArgumentNullException(nameof(fixState));

    public string Name => "fix-state";
    public string Usage => "fix-state --json <change folder>...";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0] != "--json")
        {
            throw new UsageException();
        }

        var folders = args.Skip(1).ToList();
        if (folders.Count == 1)
        {
            CliIo.PrintJson(_fixState.Compute(folders[0]));
        }
        else
        {
            var entries = _fixState.ComputeBatch(folders).Select(e =>
                e.Result is not null
                    ? (object)new { folder = e.Folder, result = e.Result }
                    : new { folder = e.Folder, error = e.Error }).ToList();
            CliIo.PrintJson(entries);
        }

        return Task.FromResult(ExitCodes.Pass);
    }
}
