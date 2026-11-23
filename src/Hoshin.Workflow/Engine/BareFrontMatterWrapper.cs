using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Engine;

/// <summary>Auto-wraps a bare line-1 JSONC object (<c>{ ... }</c>) in <c>```jsonc ... ```</c> fences when it parses cleanly as JSONC.</summary>
public static class BareFrontMatterWrapper
{
    public static string? TryAutoWrap(string text)
    {
        var normalized = Doc.Normalize(text);
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || !lines[0].TrimStart().StartsWith('{'))
        {
            return null;
        }

        var closeLine = FindObjectClosingLine(lines);
        if (closeLine < 0)
        {
            return null;
        }

        var candidate = string.Join('\n', lines[..(closeLine + 1)]);
        if (!Jsonc.TryParse(candidate, out var parsed, out _) || parsed is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        var wrapped = new List<string> { "```jsonc" };
        wrapped.AddRange(lines[..(closeLine + 1)]);
        wrapped.Add("```");
        wrapped.AddRange(lines[(closeLine + 1)..]);
        return string.Join('\n', wrapped);
    }

    private static int FindObjectClosingLine(string[] lines)
    {
        var state = new ScanState();
        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            if (ScanLine(lines[lineIdx], ref state))
            {
                return lineIdx;
            }
        }

        return -1;
    }

    private static bool ScanLine(string line, ref ScanState state)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (state.InString)
            {
                AdvanceStringChar(c, ref state);
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return false;
            }

            if (AdvanceCodeChar(c, ref state))
            {
                return true;
            }
        }

        return false;
    }

    private static void AdvanceStringChar(char c, ref ScanState state)
    {
        if (state.Escape)
        {
            state.Escape = false;
        }
        else if (c == '\\')
        {
            state.Escape = true;
        }
        else if (c == '"')
        {
            state.InString = false;
        }
    }

    private static bool AdvanceCodeChar(char c, ref ScanState state)
    {
        if (c == '"')
        {
            state.InString = true;
            return false;
        }

        if (c == '{')
        {
            state.Depth++;
            return false;
        }

        if (c == '}')
        {
            state.Depth--;
            return state.Depth == 0;
        }

        return false;
    }

    private struct ScanState
    {
        public int Depth;
        public bool InString;
        public bool Escape;
    }
}
