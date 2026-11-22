using System.Text;
using System.Text.RegularExpressions;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class InjectedTextTruncationTests
{
    private static void AssertValidUtf16(string text)
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        _ = strict.GetBytes(text);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), $"Unpaired high surrogate at index {i}");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[i]), $"Unpaired low surrogate at index {i}");
            }
        }
    }

    [Fact]
    public void HookInstructions_LargeTaskAndVerdictReport_TruncatesMiddleAndPreservesClosingMarker()
    {
        var verdictReport = "T1 attempt 1 is not accepted:\n" + new string('R', 2970) + "\n\n";
        var hugeTaskText = "START-OF-TASK\n" + new string('A', 20_000) + "\nEND-OF-TASK";
        var agent = new AgentTask("implement", "T1", 2, "# Implement one task", hugeTaskText);
        var step = new Retry(agent, ["red: failed"]);
        const string nonce = "<!-- hoshin:T1:attempt-2:deadbeef -->";

        // Build injected text using single-place budget (report + instructions + closing marker).
        var combined = HookInstructions.For(step, HookHarness.ClaudeCode, nonce, prefix: verdictReport);

        Assert.True(combined.Length <= 10_000, $"Expected <= 10,000 chars, got {combined.Length}");
        Assert.EndsWith(HookInstructions.EndMarker, combined.TrimEnd(), StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\[… \d+ characters omitted …\]"), combined);
        Assert.Contains("T1 attempt 1 is not accepted:", combined, StringComparison.Ordinal);
        Assert.Contains(nonce, combined, StringComparison.Ordinal);
        AssertValidUtf16(combined);
    }

    [Fact]
    public void TruncateIfNeeded_EmojiAtCutPoint_PreservesValidUtf16WithoutSplittingSurrogates()
    {
        // Fill with 2-char UTF-16 emoji surrogate pairs (🚀 = U+1F680) across every boundary.
        var emojiPrompt = string.Concat(Enumerable.Repeat("🚀", 6000));
        var truncated = WorkflowBriefs.TruncateIfNeeded(emojiPrompt);

        Assert.True(truncated.Length <= 10_000, $"Expected <= 10,000 chars, got {truncated.Length}");
        Assert.Matches(new Regex(@"\[… \d+ characters omitted …\]"), truncated);
        AssertValidUtf16(truncated);

        // Also test an odd-offset prefix so a surrogate pair sits squarely across the cut point.
        var oddEmojiPrompt = "X" + string.Concat(Enumerable.Repeat("🚀", 6000));
        var oddTruncated = WorkflowBriefs.TruncateIfNeeded(oddEmojiPrompt);

        Assert.True(oddTruncated.Length <= 10_000);
        AssertValidUtf16(oddTruncated);
    }
}
