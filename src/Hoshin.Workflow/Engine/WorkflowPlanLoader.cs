using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Engine;

/// <summary>Loads <see cref="WorkflowPlan"/> from <c>plan.md</c>, <c>change.jsonc</c>, <c>.hoshin.json</c> build settings, and base-branch checks.</summary>
public static class WorkflowPlanLoader
{
    public static WorkflowPlan? Load(WorkflowRepo repo, PlanReader planReader, IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(planReader);
        ArgumentNullException.ThrowIfNull(fs);

        var settings = MarkerBuildSettings.Read(repo.Root, fs);
        var planPath = Path.Combine(repo.ChangeFolder, "plan.md");
        var changePath = Path.Combine(repo.ChangeFolder, "change.jsonc");

        if (!fs.FileExists(planPath))
        {
            return LoadMetadataOnly(repo, changePath, settings, fs);
        }

        var planDocStatus = repo.DocumentStatus("plan.md");
        var read = TryReadPlanDocument(planReader, planPath, planDocStatus, fs);
        if (read is null)
        {
            return LoadMetadataOnly(repo, changePath, settings, fs);
        }

        return BuildPlan(repo, read, changePath, settings, fs);
    }

    private static PlanReadResult? TryReadPlanDocument(
        PlanReader planReader,
        string planPath,
        WorkflowDocState planDocStatus,
        IFileSystem fs)
    {
        var isDraftUncommitted = planDocStatus.Uncommitted || !planDocStatus.InHead;
        PlanReadResult read;
        try
        {
            read = planReader.Read(fs.ReadAllText(planPath), "plan.md");
        }
        catch (CouldNotRunException) when (isDraftUncommitted)
        {
            // An uncommitted draft plan (e.g. bare JSONC front matter during WritePlan/Fix) is validated at Stop.
            return null;
        }

        if (read.Problems.Count == 0)
        {
            return read;
        }

        if (isDraftUncommitted)
        {
            return null;
        }

        throw new CouldNotRunException(
            "plan.md has problems: " + string.Join("; ", read.Problems.Select(p => $"line {p.Line}: {p.Message}")));
    }

    private static WorkflowPlan BuildPlan(
        WorkflowRepo repo,
        PlanReadResult read,
        string changePath,
        MarkerBuildSettings settings,
        IFileSystem fs)
    {
        var changeObj = TryReadChangeObject(changePath, fs);
        var scope = StringOrNull(changeObj, "scope")
            ?? Jsonc.String(read.FrontMatter, "scope")
            ?? Jsonc.String(read.FrontMatter, "system")
            ?? throw new CouldNotRunException("change.jsonc lacks scope");

        var change = StringOrNull(changeObj, "change") ?? Path.GetFileName(repo.ChangeFolder);
        var baseBranch = StringOrNull(changeObj, "base") ?? "main";
        var branch = StringOrNull(changeObj, "branch");
        var workType = StringOrNull(changeObj, "type") ?? "feat";
        var tasks = read.Document.Tasks.Select(ToWorkflowTask).ToList();
        var systemName = Jsonc.String(read.FrontMatter, "system") ?? scope;

        return new WorkflowPlan(
            Change: change,
            Scope: scope,
            Base: baseBranch,
            Tasks: tasks,
            Checks: BaseBranchChecksReader.Read(repo, baseBranch, systemName),
            HasPlan: true,
            Branch: branch,
            WorkType: workType,
            TestPatterns: settings.TestPatterns,
            EnvAllow: settings.EnvAllow,
            TestTimeoutSeconds: settings.TestTimeoutSeconds,
            CheckTimeoutSeconds: settings.CheckTimeoutSeconds,
            MaxAttempts: settings.MaxAttempts,
            LinkDirs: settings.LinkDirs);
    }

    private static WorkflowPlan? LoadMetadataOnly(
        WorkflowRepo repo,
        string changePath,
        MarkerBuildSettings settings,
        IFileSystem fs)
    {
        var only = TryReadChangeObject(changePath, fs);
        if (only is null)
        {
            return null;
        }

        var changeName = Jsonc.String(only.Value, "change") ?? Path.GetFileName(repo.ChangeFolder);
        var scope = Jsonc.String(only.Value, "scope") ?? throw new CouldNotRunException("change.jsonc lacks scope");
        var baseBranch = Jsonc.String(only.Value, "base") ?? "main";
        return new WorkflowPlan(
            Change: changeName,
            Scope: scope,
            Base: baseBranch,
            Tasks: [],
            Checks: BaseBranchChecksReader.Read(repo, baseBranch, scope),
            HasPlan: false,
            Branch: Jsonc.String(only.Value, "branch"),
            WorkType: Jsonc.String(only.Value, "type") ?? "feat",
            TestPatterns: settings.TestPatterns,
            EnvAllow: settings.EnvAllow,
            TestTimeoutSeconds: settings.TestTimeoutSeconds,
            CheckTimeoutSeconds: settings.CheckTimeoutSeconds,
            MaxAttempts: settings.MaxAttempts,
            LinkDirs: settings.LinkDirs);
    }

    private static WorkflowTask ToWorkflowTask(ParsedTask t) =>
        new(
            t.Id,
            t.Name,
            string.Join("\n\n", t.Sections.Select(s => s.Body)),
            t.Test,
            t.Compile,
            t.Files,
            t.Timeout,
            t.Repo,
            t.DependsOn);

    private static JsonElement? TryReadChangeObject(string path, IFileSystem fs)
    {
        if (!fs.FileExists(path))
        {
            return null;
        }

        if (!Jsonc.TryParse(fs.ReadAllText(path), out var value, out var error))
        {
            throw new CouldNotRunException($"{Path.GetFileName(path)} is not valid JSONC: {error}");
        }

        return value is { ValueKind: JsonValueKind.Object } obj
            ? obj
            : throw new CouldNotRunException($"{Path.GetFileName(path)} is not a set of fields");
    }

    private static string? StringOrNull(JsonElement? obj, string key) =>
        obj is { } el ? Jsonc.String(el, key) : null;
}
