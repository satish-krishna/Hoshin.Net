using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Documents;

/// <summary>Abstraction for reading any hoshin Markdown document by path or content.</summary>
public interface IDocumentReader
{
    DocKind KindOf(string file);
    ReadResult ReadFile(string file, IReadOnlyList<string>? allowedPlanRepos = null, string? systemName = null);
    ReadResult ReadText(DocKind kind, string text, string file, IReadOnlyList<string>? allowedPlanRepos = null, string? systemName = null);
}

/// <summary>Dispatches document reading by file name (<c>spec.md</c>, <c>plan.md</c>, <c>build/&lt;repo&gt;.md</c>, <c>build/review.md</c>).</summary>
public sealed class DocumentReader(
    SpecReader? specReader = null,
    PlanReader? planReader = null,
    BuildLogReader? buildLogReader = null,
    ReviewReader? reviewReader = null) : IDocumentReader
{
    private readonly SpecReader _spec = specReader ?? new SpecReader();
    private readonly PlanReader _plan = planReader ?? new PlanReader();
    private readonly BuildLogReader _buildLog = buildLogReader ?? new BuildLogReader();
    private readonly ReviewReader _review = reviewReader ?? new ReviewReader();

    public DocKind KindOf(string file)
    {
        var parts = file.Split(['/', '\\']);
        var name = parts.Length > 0 ? parts[^1] : "";
        var parent = parts.Length > 1 ? parts[^2] : "";
        if (name == "spec.md")
        {
            return DocKind.Spec;
        }

        if (name == "plan.md")
        {
            return DocKind.Plan;
        }

        if (parent == "build" && name == "review.md")
        {
            return DocKind.Review;
        }

        if (parent == "build" && name.EndsWith(".md", StringComparison.Ordinal))
        {
            return DocKind.BuildLog;
        }

        throw new CouldNotRunException($"{file}: not a document this program knows (expected spec.md, plan.md, build/<repo>.md or build/review.md)");
    }

    public ReadResult ReadFile(string file, IReadOnlyList<string>? allowedPlanRepos = null, string? systemName = null)
    {
        var kind = KindOf(file);
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (FileNotFoundException)
        {
            throw new CouldNotRunException($"{file}: cannot read the file: ENOENT");
        }
        catch (DirectoryNotFoundException)
        {
            throw new CouldNotRunException($"{file}: cannot read the file: ENOENT");
        }
        catch (Exception e)
        {
            throw new CouldNotRunException($"{file}: cannot read the file: {e.Message}");
        }

        return ReadText(kind, text, file, allowedPlanRepos, systemName);
    }

    public ReadResult ReadText(DocKind kind, string text, string file, IReadOnlyList<string>? allowedPlanRepos = null, string? systemName = null) =>
        kind switch
        {
            DocKind.Spec => _spec.Read(text, file),
            DocKind.Plan => _plan.Read(text, file, allowedPlanRepos, systemName),
            DocKind.BuildLog => _buildLog.Read(text, file),
            DocKind.Review => _review.Read(text, file),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
}
