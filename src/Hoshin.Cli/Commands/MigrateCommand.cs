using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class MigrateCommand(HoshinServices services) : IHoshinCommand
{
    public string Name => "migrate";
    public string Usage => "migrate --json [--write] [--dry-run] [--documents] [--master <path>] [--workspace <path>] [<path>...]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(
            args,
            values: ["master", "workspace"],
            flags: ["json", "write", "dry-run", "documents"]);

        if (o.Has("write") && o.Has("dry-run"))
        {
            throw new UsageException();
        }

        // Direct document migration mode: `migrate <path>...` or `migrate --documents [<path>...]`.
        if (o.Has("documents") || o.Positional.Count > 0)
        {
            if (o.Get("master") is not null || o.Get("workspace") is not null)
            {
                throw new UsageException();
            }

            IReadOnlyList<string> targets = o.Positional.Count > 0
                ? o.Positional
                : [Directory.GetCurrentDirectory()];
            var write = !o.Has("dry-run");
            var docResult = services.DocumentMigrator.MigratePaths(targets, write);

            if (o.Has("json"))
            {
                CliIo.PrintJson(new
                {
                    write = docResult.Write,
                    files = docResult.Files,
                    problems = docResult.Problems,
                });
            }
            else
            {
                foreach (var item in docResult.Files)
                {
                    if (item.Status == "current")
                    {
                        continue;
                    }

                    var dest = item.To is null ? item.Path : $"{item.Path} -> {item.To}";
                    var detail = item.Detail is null ? "" : $" ({item.Detail})";
                    Console.WriteLine($"{item.Status}: {dest}{detail}");
                }

                if (docResult.Problems.Count > 0)
                {
                    foreach (var p in docResult.Problems)
                    {
                        CliIo.WriteStderrLine(p);
                    }
                }
            }

            return Task.FromResult(docResult.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
        }

        // Workspace migration mode (preserves exact existing CLI contract).
        if (!o.Has("json"))
        {
            throw new UsageException();
        }

        var result = services.Migrator.Migrate(
            Directory.GetCurrentDirectory(),
            new ResolveOptions(o.Get("master"), o.Get("workspace")),
            o.Has("write"));

        CliIo.PrintJson(new
        {
            workspace = result.Workspace,
            master = result.Master,
            layout = result.Layout.ToWireName(),
            write = result.Write,
            steps = result.Steps,
            kept = result.Kept,
            manual = result.Manual,
            problems = result.Problems,
        });
        return Task.FromResult(result.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
