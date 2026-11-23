using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class ReadCommand(IDocumentReader reader, MarkerReader markerReader, IFileSystem fileSystem) : IHoshinCommand
{
    private readonly IDocumentReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly MarkerReader _markerReader = markerReader ?? throw new ArgumentNullException(nameof(markerReader));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public string Name => "read";
    public string Usage => "read --json <file>";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || args[0] != "--json")
        {
            throw new UsageException();
        }

        var file = args[1];
        var kind = _reader.KindOf(file);
        IReadOnlyList<string>? allowed = null;
        string? system = null;
        if (kind == DocKind.Plan)
        {
            if (!_fs.FileExists(file))
            {
                throw new CouldNotRunException($"{file}: cannot read the file: ENOENT");
            }

            var marker = _markerReader.FindMarker(file);
            var fm = FrontMatter.Parse(_fs.ReadAllText(file), file);
            system = Jsonc.String(fm.Data, "system") ?? "";
            allowed = MarkerReader.AllowedPlanRepos(marker, system);
        }

        var result = _reader.ReadFile(file, allowed, system);
        CliIo.PrintRawJson(result.ToJson(indented: false));
        return Task.FromResult(result.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
