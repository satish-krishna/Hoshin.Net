using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;

namespace Hoshin.Cli.Commands;

public sealed class CheckCommand(IDocumentCheckService checker) : IHoshinCommand
{
    private readonly IDocumentCheckService _checker = checker ?? throw new ArgumentNullException(nameof(checker));

    public string Name => "check";
    public string Usage => "check <file>...";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new UsageException();
        }

        var problems = _checker.CheckFiles(args);
        foreach (var p in problems)
        {
            CliIo.WriteStdoutLine(ProblemFormatter.Format(p));
        }

        return Task.FromResult(problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
