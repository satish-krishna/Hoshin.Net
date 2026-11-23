using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Workspace;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class EnvironmentAndPublishTests
{
    [Fact]
    public void MarkerProblems_AcceptsEnvAllowListAndRejectsInvalidShapes()
    {
        Assert.True(Jsonc.TryParse("""
            {
              "version": 1,
              "systems": { "demo": { "repos": ["app"] } },
              "env": { "allow": ["CUSTOM_SDK_ROOT", "MY_PROXY"] }
            }
            """, out var valid, out _));
        Assert.Empty(MarkerReader.MarkerProblems(valid!.Value));

        Assert.True(Jsonc.TryParse("""
            {
              "version": 1,
              "systems": { "demo": { "repos": ["app"] } },
              "env": { "allow": [123] }
            }
            """, out var invalid, out _));
        Assert.NotEmpty(MarkerReader.MarkerProblems(invalid!.Value));
    }

    [Fact]
    public void WorkflowRunner_ForwardsMarkerEnvAllowToTaskTests()
    {
        var prevCustom = Environment.GetEnvironmentVariable("HOSHIN_TEST_CUSTOM_TOOL");
        try
        {
            Environment.SetEnvironmentVariable("HOSHIN_TEST_CUSTOM_TOOL", "enabled-42");

            using var tmp = new TempDir("hoshin-t17-env-");
            var repoDir = Path.Combine(tmp.Path, "repo");
            TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
            {
                [".hoshin.json"] = """
                    {
                      "version": 1,
                      "systems": { "demo": { "repos": ["repo"] } },
                      "env": { "allow": ["HOSHIN_TEST_CUSTOM_TOOL"] }
                    }
                    """,
                ["src/math.mjs"] = "export function add(a, b) { return a + b; }\n",
                [".hoshin/change.jsonc"] = """
                    { "version": 1, "change": "t17-env", "type": "feat", "scope": "math", "base": "main" }
                    """,
                [".hoshin/plan.md"] = """
                    ```jsonc
                    { "layout": 2, "system": "math", "status": "accepted" }
                    ```

                    # Plan: Env

                    ## Tasks

                    ### T1: Needs custom env

                    ```jsonc
                    { "id": "T1", "repo": "repo", "test": ["node", "--test", "test/t1.test.mjs"] }
                    ```

                    #### Change
                    Add `sub`.

                    #### Tests
                    Verify `HOSHIN_TEST_CUSTOM_TOOL` is forwarded and `sub` works.
                    """,
            });

            var repo = TestServices.FindRepo(repoDir)!;
            var runner = TestServices.WorkflowRunner;
            var state = TestServices.ReadState(repo)!;
            var step = Assert.IsType<Implement>(WorkflowEngine.Next(state));
            runner.Issue(repo, state, step, "s1");

            TestGitFixture.Write(
                Path.Combine(repoDir, "test", "t1.test.mjs"),
                "import test from 'node:test'; import assert from 'node:assert/strict'; import { sub } from '../src/math.mjs'; test('env', () => { assert.equal(process.env.HOSHIN_TEST_CUSTOM_TOOL, 'enabled-42'); assert.equal(sub(5, 2), 3); });\n");
            TestGitFixture.Write(
                Path.Combine(repoDir, "src", "math.mjs"),
                "export function add(a, b) { return a + b; }\nexport function sub(a, b) { return a - b; }\n");

            var verdict = runner.EvaluateAndCommitTask(repo, TestServices.ReadState(repo)!, "T1", 1);
            Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOSHIN_TEST_CUSTOM_TOOL", prevCustom);
        }
    }

    [Fact]
    public void Publish_ToNonExistentRemote_ReturnsGitErrorOutput()
    {
        using var tmp = new TempDir("hoshin-t17-pub-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export const x = 1;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t17-pub", "type": "feat", "scope": "math", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "math", "status": "accepted" }
                ```

                # Plan: Pub

                ## Tasks

                ### T1: Dummy

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "-e", "process.exit(0)"] }
                ```

                #### Tests
                None.
                """,
        });

        TestGitFixture.Git(repoDir, "remote", "set-url", "origin", Path.Combine(tmp.Path, "does-not-exist.git"));

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo)!;
        var result = TestServices.WorkflowRunner.Publish(repo, state, "refs/heads/hoshin/t17-pub");

        Assert.False(result.Ok);
        Assert.Null(result.Published);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Contains("does-not-exist.git", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectPushEnvironment_KeepsSshAndGcmVarsOutsideTaskAllowList()
    {
        var prevSsh = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        var prevGcm = Environment.GetEnvironmentVariable("GCM_INTERACTIVE");
        try
        {
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", "/tmp/agent.sock");
            Environment.SetEnvironmentVariable("GCM_INTERACTIVE", "never");

            var pushEnv = AllowListedCommandRunner.CollectPushEnvironment();
            Assert.Equal("/tmp/agent.sock", pushEnv["SSH_AUTH_SOCK"]);
            Assert.Equal("never", pushEnv["GCM_INTERACTIVE"]);
            Assert.DoesNotContain("SSH_AUTH_SOCK", AllowListedCommandRunner.AllowedEnvironmentVariables);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", prevSsh);
            Environment.SetEnvironmentVariable("GCM_INTERACTIVE", prevGcm);
        }
    }
}
