using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Changes;

public sealed record ChangeDoc(string File, ReadResult Result);

/// <summary>Cross-document validation: plan tasks against spec scenarios/rules, plan self-references, and build logs against plan tasks.</summary>
public static class CrossChecker
{
    public static List<Problem> CheckChange(IReadOnlyList<ChangeDoc> docs)
    {
        (ParsedSpec Doc, string File)? spec = null;
        (ParsedPlan Doc, string File, int Layout)? plan = null;
        var logs = new List<(ParsedBuildLog Doc, string File)>();

        foreach (var d in docs)
        {
            switch (d.Result)
            {
                case SpecReadResult s:
                    spec = (s.Document, d.File);
                    break;
                case PlanReadResult p:
                    plan = (p.Document, d.File, p.Layout);
                    break;
                case BuildLogReadResult b:
                    logs.Add((b.Document, d.File));
                    break;
            }
        }

        var problems = new List<Problem>();
        if (plan is null)
        {
            return problems;
        }

        if (plan.Value.Layout >= 2)
        {
            var checkSpec = IsSuperseded(plan.Value.File) ? null : spec;
            problems.AddRange(CheckPlan(plan.Value.Doc, plan.Value.File, checkSpec));
        }

        foreach (var log in logs)
        {
            problems.AddRange(CheckLog(log.Doc, log.File, plan.Value.Doc));
        }

        return problems;
    }

    private static int FieldLine(ParsedTask task, string key)
    {
        var re = new Regex($"^\\s*\"{Regex.Escape(key)}\"\\s*:");
        var lines = task.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (re.IsMatch(lines[i]))
            {
                return task.Line + i;
            }
        }

        return task.Line;
    }

    private static HashSet<string> SpecIds(ParsedSpec spec)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in spec.Requirements)
        {
            ids.Add(r.Id);
            foreach (var s in r.Scenarios)
            {
                ids.Add(s.Id);
            }
        }

        return ids;
    }

    private static List<Problem> CheckPlan(ParsedPlan plan, string planFile, (ParsedSpec Doc, string File)? spec)
    {
        var problems = new List<Problem>();
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Tasks.Count; i++)
        {
            var t = plan.Tasks[i];
            if (position.ContainsKey(t.Id))
            {
                problems.Add(new Problem(planFile, t.Line, $"task ID {t.Id} is used twice"));
            }
            else
            {
                position[t.Id] = i;
            }
        }

        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in plan.SharedDecisions)
        {
            owners[d.Question] = d.Owner;
        }

        var ids = spec is null ? null : SpecIds(spec.Value.Doc);
        var covered = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < plan.Tasks.Count; i++)
        {
            var t = plan.Tasks[i];
            foreach (var dep in t.DependsOn)
            {
                if (!position.TryGetValue(dep, out var at))
                {
                    problems.Add(new Problem(planFile, FieldLine(t, "depends_on"), $"{t.Id} depends on {dep}, which is not in the plan"));
                }
                else if (at > i)
                {
                    problems.Add(new Problem(planFile, FieldLine(t, "depends_on"), $"{t.Id} depends on {dep}, which comes after it"));
                }
            }

            foreach (var id in t.Covers)
            {
                if (ids is not null && !ids.Contains(id))
                {
                    problems.Add(new Problem(planFile, FieldLine(t, "covers"), $"{t.Id} covers {id}, which is not in the spec"));
                }

                covered.Add(id);
            }

            foreach (var id in t.Supports)
            {
                if (ids is not null && !ids.Contains(id))
                {
                    problems.Add(new Problem(planFile, FieldLine(t, "supports"), $"{t.Id} supports {id}, which is not in the spec"));
                }
            }

            foreach (var question in t.Uses)
            {
                if (!owners.TryGetValue(question, out var owner))
                {
                    problems.Add(new Problem(planFile, FieldLine(t, "uses"), $"{t.Id} uses \"{question}\", which no shared decision owns"));
                }
                else if (!t.DependsOn.Contains(owner) && owner != t.Id)
                {
                    problems.Add(new Problem(planFile, FieldLine(t, "uses"), $"{t.Id} uses \"{question}\" but does not depend on {owner}"));
                }
            }
        }

        if (spec is not null)
        {
            foreach (var r in spec.Value.Doc.Requirements)
            {
                foreach (var s in r.Scenarios)
                {
                    if (!covered.Contains(s.Id) && !covered.Contains(r.Id))
                    {
                        problems.Add(new Problem(spec.Value.File, s.Line, $"scenario {s.Id} is covered by no task"));
                    }
                }
            }
        }

        return problems;
    }

    private static List<Problem> CheckLog(ParsedBuildLog log, string logFile, ParsedPlan plan)
    {
        var problems = new List<Problem>();
        var byId = new Dictionary<string, ParsedTask>(StringComparer.Ordinal);
        foreach (var t in plan.Tasks)
        {
            byId.TryAdd(t.Id, t);
        }

        foreach (var e in log.Entries)
        {
            if (!byId.TryGetValue(e.Task, out var task))
            {
                problems.Add(new Problem(logFile, e.Line, $"build log entry {e.Task} is not a task in the plan"));
            }
            else if (task.Repo.Length > 0 && log.Repo.Length > 0 && task.Repo != log.Repo)
            {
                problems.Add(new Problem(logFile, e.Line, $"build log entry {e.Task} belongs to repository {task.Repo}"));
            }
        }

        return problems;
    }

    private static bool IsSuperseded(string file)
    {
        try
        {
            return FrontMatter.TryGetScalar(File.ReadAllText(file), file, "status") == "superseded";
        }
        catch
        {
            return false;
        }
    }
}
