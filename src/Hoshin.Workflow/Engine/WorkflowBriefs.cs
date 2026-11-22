using System.Globalization;
using System.Text;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Builds the harness-neutral prompt and output contract handed to an agent for a given step.
/// Role procedures live in <c>skills/&lt;role&gt;/procedure.md</c> and never mention CLI commands, hooks, or harness tools.
/// </summary>
public static class WorkflowBriefs
{
    public const int MaxInjectedChars = 9800;

    private const string DefaultImplementProcedure = """
        # Implement one task, test first

        You are building one small task in an existing codebase. The brief below names the task, the test command that proves it, and what to hand back.

        1. Read the code the task touches and the tests next to it before writing anything. Match the style you find.
        2. Write the test that proves the task, and only that test. Run the task's test command and confirm it fails for the reason the task describes: a failing assertion or a missing target symbol, never a syntax error.
        3. Write the smallest change to the production code that makes the test pass. Do not weaken or remove the test to make it pass.
        4. Run the task's test command again and confirm it passes. Run related tests too, if they are quick, and leave them passing.
        5. Leave your changes in the working tree without committing or pushing; the workflow records the test and implementation commits for you after verification.

        Keep the change inside what the task asks for. If the task cannot be done as written, stop after step 1 and say why in your reply instead of guessing.
        """;

    public static AgentTask Implement(WorkflowState state, WorkflowTask task, int attempt, IReadOnlyList<string> handoff)
    {
        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"# Task {task.Id}: {task.Title}");
        b.AppendLine();
        b.AppendLine(task.Text);
        b.AppendLine();
        b.AppendLine(CultureInfo.InvariantCulture, $"Repository: {state.RepoRoot}");
        b.AppendLine(CultureInfo.InvariantCulture, $"Test command for this task: `{string.Join(' ', task.Test)}`");
        if (task.Compile is { Length: > 0 })
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"Compile command for this task: `{string.Join(' ', task.Compile)}`");
        }

        b.AppendLine();
        b.AppendLine("## What to hand back");
        b.AppendLine();
        b.AppendLine("1. Write the new failing test first, verify it fails for the right reason, then implement the production change so the test passes.");
        b.AppendLine("2. Edit only the working tree. Do not commit, stage, or push, and do not edit `.hoshin/`, `.git/`, or `.agents/`.");
        b.AppendLine("3. Reply with two or three sentences summarizing what you changed and what test proves it.");

        if (handoff.Count > 0)
        {
            b.AppendLine();
            b.AppendLine(CultureInfo.InvariantCulture, $"## Attempt {attempt}: the previous attempt was not accepted");
            b.AppendLine();
            foreach (var reason in handoff)
            {
                b.AppendLine(CultureInfo.InvariantCulture, $"- {reason}");
            }

            b.AppendLine();
            b.AppendLine("Nothing was committed from the previous attempt. Your working-tree edits remain; fix the issues above and leave the tests passing.");
        }

        return new AgentTask("implement", task.Id, attempt, LoadRoleProcedure(state.SkillsRoot, "implement"), b.ToString());
    }

    public static string Intent(WorkflowState state)
    {
        var rel = RelativePath(state.RepoRoot, state.Intent.Path);
        var (systemName, contextDir) = ResolveSystem(state);
        var context = FindContextFiles(state, contextDir);

        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"# The intent for change {state.Plan.Change}");
        b.AppendLine();
        b.AppendLine(CultureInfo.InvariantCulture, $"Write it to `{rel}` in {state.RepoRoot}. Read first: {(context.Count == 0 ? "no context files exist" : string.Join(", ", context.Select(c => $"`{c}`")))}.");
        b.AppendLine();
        b.AppendLine("The file starts on line 1 with a fenced `jsonc` front matter block:");
        b.AppendLine();
        b.AppendLine("```jsonc");
        b.AppendLine(CultureInfo.InvariantCulture, $"{{ \"title\": \"<short title>\", \"system\": \"{systemName}\", \"author\": \"<name and role>\", \"date\": \"<YYYY-MM-DD>\", \"status\": \"draft\", \"scope\": \"{state.Plan.Scope}\" }}");
        b.AppendLine("```");
        b.AppendLine();
        b.AppendLine("Then include these headings in order, each with content: `# Intent: <title>`, `## Problem`, `## Proposed outcome`, `## Affected users and systems`, `## Constraints`, `## Decisions`, `## Open questions`.");
        b.AppendLine();
        b.AppendLine("Edit no other file and do not commit: the file is committed for you when you end your turn.");
        return b.ToString();
    }

    public static string Spec(WorkflowState state)
    {
        var specRel = RelativePath(state.RepoRoot, state.Spec.Path);
        var intentRel = RelativePath(state.RepoRoot, state.Intent.Path);
        var (systemName, _) = ResolveSystem(state);
        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"# The spec for change {state.Plan.Change}");
        b.AppendLine();
        b.AppendLine(CultureInfo.InvariantCulture, $"Write it to `{specRel}` in {state.RepoRoot}, from the accepted intent `{intentRel}`.");
        b.AppendLine();
        b.AppendLine("The file starts on line 1 with a fenced `jsonc` front matter block:");
        b.AppendLine();
        b.AppendLine("```jsonc");
        b.AppendLine(CultureInfo.InvariantCulture, $"{{ \"layout\": 2, \"title\": \"<intent title>\", \"system\": \"{systemName}\", \"date\": \"<YYYY-MM-DD>\", \"status\": \"draft\" }}");
        b.AppendLine("```");
        b.AppendLine();
        b.AppendLine("Then `# Spec: <title>` and `## Requirements` with one fenced `gherkin` block: a `Feature:`, one `Rule:` per requirement tagged `@R<n>` and at least one `@from:<source>`, and scenarios tagged `@R<n>.<m>`.");
        b.AppendLine();
        b.AppendLine("Edit no other file and do not commit: the file is committed for you when you end your turn.");
        return b.ToString();
    }

    public static string Plan(WorkflowState state)
    {
        var planRel = RelativePath(state.RepoRoot, state.PlanDoc.Path);
        var specRel = RelativePath(state.RepoRoot, state.Spec.Path);
        var (systemName, _) = ResolveSystem(state);
        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"# The plan for change {state.Plan.Change}");
        b.AppendLine();
        b.AppendLine(CultureInfo.InvariantCulture, $"Write it to `{planRel}` in {state.RepoRoot}, from the accepted spec `{specRel}`.");
        b.AppendLine();
        b.AppendLine("The file starts on line 1 with a fenced `jsonc` front matter block:");
        b.AppendLine();
        b.AppendLine("```jsonc");
        b.AppendLine(CultureInfo.InvariantCulture, $"{{ \"layout\": 2, \"system\": \"{systemName}\", \"date\": \"<YYYY-MM-DD>\", \"status\": \"draft\" }}");
        b.AppendLine("```");
        b.AppendLine();
        b.AppendLine("Define `## Tasks` with `### T<n>: <name>` headings, each followed by a fenced `jsonc` fields block (`id`, `repo`, `depends_on`, `covers`, `test`) and `#### Change` / `#### Tests` sections.");
        b.AppendLine();
        b.AppendLine("Edit no other file and do not commit: the file is committed for you when you end your turn.");
        return b.ToString();
    }

    private static (string SystemName, string? ContextDir) ResolveSystem(WorkflowState state)
    {
        var rel = RelativePath(state.RepoRoot, state.ChangeFolder).Trim('/');
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 &&
            parts[0].Equals("intent", StringComparison.OrdinalIgnoreCase) &&
            !parts[1].Equals("hooks", StringComparison.OrdinalIgnoreCase))
        {
            var system = parts[1];
            var contextDir = string.IsNullOrEmpty(state.RepoRoot)
                ? Path.Combine("intent", system, "context")
                : Path.Combine(state.RepoRoot, "intent", system, "context");
            return (system, contextDir);
        }

        return (state.Plan.Scope, null);
    }

    private static List<string> FindContextFiles(WorkflowState state, string? systemContextDir)
    {
        var files = new List<string>();
        if (systemContextDir is not null && Directory.Exists(systemContextDir))
        {
            files.AddRange(Directory.GetFiles(systemContextDir, "*.md"));
        }
        else if (Directory.Exists(state.ChangeFolder))
        {
            files.AddRange(Directory.GetFiles(state.ChangeFolder, "context*.md"));
        }

        return files
            .Select(f => RelativePath(state.RepoRoot, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    public static string Review(WorkflowState state)
    {
        var reviewPath = Path.Combine(state.ChangeFolder, "build", "review.md");
        var reviewRel = RelativePath(state.RepoRoot, reviewPath);
        var nextNum = state.Ledger.Reviews.Count + 1;
        var shortHead = WorkflowEngine.Short(state.Head);

        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"# Review for change {state.Plan.Change} (Review {nextNum})");
        b.AppendLine();
        b.AppendLine(CultureInfo.InvariantCulture, $"Write the review section to `{reviewRel}` in {state.RepoRoot} (head `{shortHead}`).");
        b.AppendLine();
        b.AppendLine("Start a new file on line 1 with a fenced `jsonc` front matter block `{ \"layout\": 2 }`, then `# Review findings`, then `## Review " + nextNum + "` with a fenced `jsonc` header (`review`, `reviewed_at`, `commits`) and `### F<n>` findings (`id`, `severity`, `concern`, `file`, `line`, plus `rule` for every `convention` finding).");
        b.AppendLine();
        b.AppendLine("Edit no product code and do not commit: `build/review.md` is validated and committed for you when you end your turn.");
        return b.ToString();
    }

    public static string Fix(WorkflowState state)
    {
        var intentRel = RelativePath(state.RepoRoot, state.Intent.Path);
        var specRel = RelativePath(state.RepoRoot, state.Spec.Path);
        var planRel = RelativePath(state.RepoRoot, state.PlanDoc.Path);

        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"# Light-track fix for change {state.Plan.Change}");
        b.AppendLine();
        b.AppendLine(CultureInfo.InvariantCulture, $"Write all three change documents in {state.RepoRoot}: `{intentRel}`, `{specRel}`, and `{planRel}`.");
        b.AppendLine();
        b.AppendLine("Light-track boundary: one repository, no business rule changes, at most 3 tasks, and 1 new behavior per task.");
        b.AppendLine("Use fenced `jsonc` front matter on line 1 (`status: \"draft\"`, `scope: \"" + state.Plan.Scope + "\"`, `layout: 2` on spec and plan).");
        b.AppendLine();
        b.AppendLine("Edit no product code and do not commit: all three documents are validated, pinned, and committed for you when you end your turn.");
        return b.ToString();
    }

    public static string LoadRoleProcedure(string? skillsRoot, string role)
    {
        if (!string.IsNullOrWhiteSpace(skillsRoot))
        {
            var path = Path.Combine(skillsRoot, role, "procedure.md");
            if (File.Exists(path))
            {
                return StripAnyLeadingFrontMatter(File.ReadAllText(path));
            }
        }

        // Fallback to embedded procedure or built-in default.
        var asm = typeof(WorkflowBriefs).Assembly;
        var suffix = $".skills.{role}.procedure.md";
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resName is not null)
        {
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return StripAnyLeadingFrontMatter(reader.ReadToEnd());
            }
        }

        return role.Equals("implement", StringComparison.OrdinalIgnoreCase)
            ? DefaultImplementProcedure
            : $"Follow the {role} role instructions provided in context.";
    }

    public static string TruncateIfNeeded(string text, int maxChars = MaxInjectedChars) =>
        TextTruncator.TruncateMiddle(text, maxChars);

    private static string StripAnyLeadingFrontMatter(string raw)
    {
        var text = raw.Replace("\r\n", "\n");
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                text = text[(end + 5)..];
            }
        }

        return text.Trim();
    }

    private static string RelativePath(string root, string full) =>
        string.IsNullOrEmpty(root) ? full.Replace('\\', '/') : Path.GetRelativePath(root, full).Replace('\\', '/');
}
