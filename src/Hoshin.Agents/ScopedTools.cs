using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Hoshin.Agents;

/// <summary>Builds the Microsoft.Extensions.AI tools an agent gets for one piece of work, all bound to a <see cref="ScopedWorkspace"/>.</summary>
public static class ScopedTools
{
    public const string ReadFile = "read_file";
    public const string ListFiles = "list_files";
    public const string WriteFile = "write_file";
    public const string DeleteFile = "delete_file";
    public const string RunCommand = "run_command";
    public const string AskPerson = "ask_person";

    /// <summary>
    /// The file and command tools for <paramref name="workspace"/>. When <paramref name="ask"/> is given, the agent also gets
    /// <c>ask_person</c>, for work that interviews a person (an intent).
    /// </summary>
    public static IList<AITool> For(ScopedWorkspace workspace, Func<string, CancellationToken, Task<string>>? ask = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                ([Description("Path relative to the working directory.")] string path) => workspace.ReadFile(path),
                ReadFile,
                "Reads a text file in the working directory."),
            AIFunctionFactory.Create(
                ([Description("Directory relative to the working directory; '.' for the root.")] string directory) => workspace.ListFiles(directory),
                ListFiles,
                "Lists the files under a directory of the working directory."),
            AIFunctionFactory.Create(
                ([Description("Path relative to the working directory.")] string path, [Description("The whole new file content.")] string content) => workspace.WriteFile(path, content),
                WriteFile,
                "Creates or replaces a file. Only files this work may write are accepted."),
            AIFunctionFactory.Create(
                ([Description("Path relative to the working directory.")] string path) => workspace.DeleteFile(path),
                DeleteFile,
                "Deletes a file this work may write."),
        };

        if (workspace.Scope.MayRun.Count > 0)
        {
            var allowed = string.Join("; ", workspace.Scope.MayRun.Select(c => "[" + string.Join(", ", c.Select(a => $"\"{a}\"")) + "]"));
            tools.Add(AIFunctionFactory.Create(
                ([Description("The command as an argument list, exactly as allowed.")] string[] command) => workspace.RunCommand(command),
                RunCommand,
                $"Runs one of this work's commands in the working directory and returns its exit status and output. Allowed: {allowed}."));
        }

        if (ask is not null)
        {
            tools.Add(AIFunctionFactory.Create(
                ([Description("One question, in plain prose, with a sensible default the person can accept.")] string question, CancellationToken cancellationToken) => ask(question, cancellationToken),
                AskPerson,
                "Asks the person one question and returns their answer."));
        }

        return tools;
    }
}
