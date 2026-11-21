using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Events.Steps;

/// <summary><c>doc.accept</c>: records acceptance of <c>intent.md</c>, <c>spec.md</c>, or <c>plan.md</c>.</summary>
public sealed class DocAcceptStep(IGitClient? git = null) : IEventStep
{
    private static readonly HashSet<string> Documents = ["intent.md", "spec.md", "plan.md"];
    private readonly IGitClient _git = git ?? ProcessGitClient.Default;

    public string EventName => "doc.accept";
    public bool ReusePassed => true;

    public Task<List<string>> CheckAsync(EventContext ctx)
    {
        var doc = DocumentOf(ctx);
        if (!Documents.Contains(doc))
        {
            return Task.FromResult(new List<string> { "doc.accept needs --document intent.md, spec.md or plan.md" });
        }

        var sha = _git.Out(ctx.Master, ["log", "-1", "--format=%H", "--", Path.Combine(ctx.Folder, doc)]);
        if (string.IsNullOrEmpty(sha))
        {
            return Task.FromResult(new List<string> { $"{doc} of {ctx.Change} is not committed" });
        }

        return Task.FromResult(new List<string>());
    }

    public string Key(EventContext ctx) =>
        _git.Out(ctx.Master, ["log", "-1", "--format=%H", "--", Path.Combine(ctx.Folder, DocumentOf(ctx))]) ?? "";

    public string? Subject(EventContext ctx) => DocumentOf(ctx);

    private static string DocumentOf(EventContext ctx) => ctx.Extra.GetValueOrDefault("document", "");
}
