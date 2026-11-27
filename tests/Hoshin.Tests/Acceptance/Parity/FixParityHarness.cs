using System.Text;
using System.Text.RegularExpressions;
using Hoshin.Tests.Acceptance.Harness;
using Xunit;

namespace Hoshin.Tests.Acceptance.Parity;

/// <summary>The light-track change a test works on: its copy of the repository and its three documents.</summary>
internal sealed record FixChange(string Name, string Root, DocumentRef Intent, DocumentRef Spec, DocumentRef Plan)
{
    public string Folder => Plan.Folder;

    public DocumentRef[] Documents => [Intent, Spec, Plan];
}

/// <summary>Workspace states and observations the light-track parity tests share, built on the public harness API.</summary>
internal static class FixParityHarness
{
    public const string Request = "greet('  Ada ') keeps the padding around the name; it should trim it";

    private static readonly Regex AcceptSubject = new(@"^docs\([^)]*\): accept (intent|spec|plan) ", RegexOptions.Compiled);

    /// <summary>The single-repository workspace the light track runs in (R21 (6)).</summary>
    public static AcceptanceWorkspace CreateWorkspace() =>
        AcceptanceWorkspace.Create(WorkspaceSpec.SingleRepoShop(ChangeScripts.TrimTheGreetingName()));

    /// <summary>The engineer types <c>/hoshin:fix</c> with a request and the compliant model drafts the three documents, which the Stop hook commits.</summary>
    public static FixChange Draft(this AcceptanceWorkspace ws, Session dev)
    {
        dev.Turn(dev.Prompt("fix", Request));
        Assert.True(dev.LastDocument is not null, dev.Transcript("/hoshin:fix must ask for the light-track documents"));
        var fix = ws.CurrentChange();
        ws.AssertCommitted(fix.Plan, dev.Transcript("the light-track documents are committed when the turn ends"));
        return fix;
    }

    /// <summary>The one change of the workspace, found from its copy.</summary>
    public static FixChange CurrentChange(this AcceptanceWorkspace ws)
    {
        var copy = ws.ChangeCopy() ?? throw new InvalidOperationException("the workspace has no change copy");
        var name = Path.GetFileName(copy);
        var system = ws.Spec.SystemName;
        DocumentRef Doc(string file) => new(copy, Path.Combine("intent", system, name, file));
        return new FixChange(name, copy, Doc("intent.md"), Doc("spec.md"), Doc("plan.md"));
    }

    /// <summary>The state a drafting run leaves when it stops after the model wrote the documents and before the Stop hook committed them.</summary>
    public static FixChange DraftWrittenButNotCommitted(this AcceptanceWorkspace ws, Session dev)
    {
        var brief = dev.Prompt("fix", Request);
        Assert.True(brief is not null, dev.Transcript("/hoshin:fix must reach the plugin"));
        var fix = ws.CurrentChange();
        ws.WriteFixDocuments(fix.Documents, brief!);
        return fix;
    }

    public static string Rel(DocumentRef doc) => doc.Relative.Replace('\\', '/');

    /// <summary>What the repository's HEAD holds of a document.</summary>
    public static string AtHead(this AcceptanceWorkspace ws, DocumentRef doc) => ws.Git(doc.Root, "show", $"HEAD:{Rel(doc)}");

    /// <summary>The commit subjects the change made on its branch, newest first.</summary>
    public static string[] ChangeSubjects(this AcceptanceWorkspace ws, string root) =>
        ws.Git(root, "log", "--format=%s", "main..HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public static string[] AcceptCommits(this AcceptanceWorkspace ws, string root) =>
        ws.ChangeSubjects(root).Where(s => AcceptSubject.IsMatch(s)).ToArray();

    public static string[] CommitsWith(this AcceptanceWorkspace ws, string root, string words) =>
        ws.ChangeSubjects(root).Where(s => s.Contains(words, StringComparison.Ordinal)).ToArray();

    /// <summary>What a light-track document says about its own status at HEAD.</summary>
    public static bool IsStatus(this AcceptanceWorkspace ws, DocumentRef doc, string status) =>
        ws.HeadStatusIs(doc with { Relative = Rel(doc) }, status);

    /// <summary>Everything the hooks told the session, for message assertions.</summary>
    public static string Told(this Session session, string? lastContext) => string.Join("\n", session.Told.Append(lastContext ?? ""));

    /// <summary>The engineer edits a document in the working tree and does not commit.</summary>
    public static void Edit(DocumentRef doc, Func<string, string> change)
    {
        var text = File.ReadAllText(doc.FullPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        var edited = change(text);
        Assert.NotEqual(text, edited);
        File.WriteAllText(doc.FullPath, edited, new UTF8Encoding(false));
    }

    /// <summary>The engineer commits a hand edit under a subject of their own.</summary>
    public static void HandCommit(this AcceptanceWorkspace ws, FixChange fix, string subject, params DocumentRef[] docs)
    {
        ws.Git(fix.Root, ["add", "--", .. docs.Select(Rel)]);
        ws.Git(fix.Root, ["commit", "-q", "-m", subject, "--", .. docs.Select(Rel)]);
    }

    /// <summary>The engineer adds a line to the spec's out-of-scope list and does not commit it.</summary>
    public static void EditSpecByHand(FixChange fix) =>
        Edit(fix.Spec, t => t.TrimEnd() + "\n\n## Out of scope\n\n- A greeting for a visitor who gave a nickname instead of a first name.\n");

    /// <summary>The engineer grows the plan to <paramref name="total"/> tasks.</summary>
    public static void GrowPlan(FixChange fix, int total) =>
        Edit(fix.Plan, t =>
        {
            var b = new StringBuilder(t.TrimEnd());
            for (var n = 2; n <= total; n++)
            {
                b.Append($$"""


                    ### T{{n}}: Harden the greeting, step {{n}}

                    ```jsonc
                    {
                      "id": "T{{n}}",
                      "repo": "app",
                      "depends_on": [],
                      "covers": [],
                      "supports": ["R1.1"],
                      "test": ["node", "--test", "test/greet-trim.test.mjs"]
                    }
                    ```

                    #### Change

                    Harden the greeting around the trimmed name, step {{n}}.

                    #### Tests

                    `test/greet-step-{{n}}.test.mjs` checks step {{n}} and fails before the change.
                    """);
            }

            return b.ToString() + "\n";
        });

    /// <summary>
    /// The engineer widens the spec with a second scenario that the plan's one task covers with an existing, passing test
    /// (a protecting test) beside its one new failing test.
    /// </summary>
    public static void AddProtectingTest(FixChange fix)
    {
        Edit(fix.Spec, t =>
        {
            var close = t.LastIndexOf("```", StringComparison.Ordinal);
            const string Second = "\n    @R1.2\n    Scenario: Greeting a name without padding\n      Given the name \"Ada\"\n      When the visitor is greeted\n      Then the result is \"Hello, Ada\"\n";
            return t[..close] + Second + t[close..];
        });
        Edit(fix.Plan, t => t
            .Replace("\"covers\": [\"R1.1\"]", "\"covers\": [\"R1.1\", \"R1.2\"]", StringComparison.Ordinal)
            .Replace(
                "`test/greet-trim.test.mjs` checks `greet('  Ada ') === 'Hello, Ada'`.",
                "The one new failing test: `test/greet-trim.test.mjs` checks `greet('  Ada ') === 'Hello, Ada'` and fails before the change. For R1.2, `test/greet.test.mjs` already checks `greet('Ada') === 'Hello, Ada'`; it passes before the change and protects the unpadded greeting, so it is not a new test.",
                StringComparison.Ordinal));
    }

    /// <summary>A typed accept is the engineer's own: no document may carry an advance acceptance.</summary>
    public static void AssertNoAdvanceAcceptance(AcceptanceWorkspace ws, FixChange fix)
    {
        foreach (var doc in fix.Documents)
        {
            Assert.DoesNotContain("accepted_in_advance", ws.AtHead(doc), StringComparison.Ordinal);
        }
    }

    /// <summary>Whether a document's pin names the last commit that touched the document above it.</summary>
    public static void AssertPinsHold(this AcceptanceWorkspace ws, FixChange fix, string because)
    {
        string Pin(DocumentRef doc, string key)
        {
            var m = Regex.Match(ws.AtHead(doc), $"\"{key}\"\\s*:\\s*\"(?<sha>[0-9a-f]{{40}})\"");
            Assert.True(m.Success, $"{because}: {Rel(doc)} at HEAD has no `{key}` pin");
            return m.Groups["sha"].Value;
        }

        string Last(DocumentRef doc) => ws.Git(fix.Root, "log", "-1", "--format=%H", "--", Rel(doc));

        Assert.Equal(Last(fix.Intent), Pin(fix.Spec, "source_intent_commit"));
        Assert.Equal(Last(fix.Spec), Pin(fix.Plan, "source_spec_commit"));
    }
}
