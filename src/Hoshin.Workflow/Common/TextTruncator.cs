using System.Globalization;

namespace Hoshin.Workflow.Common;

/// <summary>
/// Truncates text on Unicode text-element (<see cref="StringInfo"/>) boundaries so surrogate pairs and
/// combining sequences are never split, preserving both start and end context with an omission marker.
/// </summary>
public static class TextTruncator
{
    public static string TruncateMiddle(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return "";
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var starts = StringInfo.ParseCombiningCharacters(text);
        var worstMarker = $"\n[… {text.Length} characters omitted …]\n";
        if (maxChars <= worstMarker.Length)
        {
            var end = PrefixBoundary(text.Length, starts, maxChars);
            return text[..end];
        }

        var bodyBudget = maxChars - worstMarker.Length;
        var headBudget = bodyBudget / 2;
        var tailBudget = bodyBudget - headBudget;

        var headEnd = PrefixBoundary(text.Length, starts, headBudget);
        var tailStart = SuffixBoundary(text.Length, starts, text.Length - tailBudget);
        if (tailStart <= headEnd)
        {
            return text;
        }

        var omitted = tailStart - headEnd;
        var marker = string.Create(CultureInfo.InvariantCulture, $"\n[… {omitted} characters omitted …]\n");
        return string.Concat(text.AsSpan(0, headEnd), marker, text.AsSpan(tailStart));
    }

    public static string TruncatePrefix(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return "";
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var starts = StringInfo.ParseCombiningCharacters(text);
        var end = PrefixBoundary(text.Length, starts, maxChars);
        return text[..end];
    }

    private static int PrefixBoundary(int textLength, int[] starts, int maxIndex)
    {
        if (maxIndex >= textLength)
        {
            return textLength;
        }

        var best = 0;
        for (var i = 0; i < starts.Length; i++)
        {
            var nextEnd = i + 1 < starts.Length ? starts[i + 1] : textLength;
            if (nextEnd <= maxIndex)
            {
                best = nextEnd;
            }
            else
            {
                break;
            }
        }

        return best;
    }

    private static int SuffixBoundary(int textLength, int[] starts, int minIndex)
    {
        if (minIndex <= 0)
        {
            return 0;
        }

        for (var i = 0; i < starts.Length; i++)
        {
            if (starts[i] >= minIndex)
            {
                return starts[i];
            }
        }

        return textLength;
    }
}
