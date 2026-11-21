using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class ReadCommand(HoshinServices services) : IHoshinCommand
{
    public string Name => "read";
    public string Usage => "read --json <file>";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || args[0] != "--json")
        {
            throw new UsageException();
        }

        var file = args[1];
        var kind = services.DocumentReader.KindOf(file);
        IReadOnlyList<string>? allowed = null;
        string? system = null;
        if (kind == DocKind.Plan)
        {
            if (!File.Exists(file))
            {
                throw new CouldNotRunException($"{file}: cannot read the file: ENOENT");
            }

            var marker = MarkerReader.FindMarker(file);
            var fm = FrontMatter.Parse(File.ReadAllText(file), file);
            system = Jsonc.String(fm.Data, "system") ?? "";
            allowed = MarkerReader.AllowedPlanRepos(marker, system);
        }

        var result = services.DocumentReader.ReadFile(file, allowed, system);
        CliIo.PrintRawJson(result.ToJson(indented: false));
        return Task.FromResult(result.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
