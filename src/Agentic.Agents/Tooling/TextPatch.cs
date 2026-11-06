using Agentic.Orchestration.Workspace;

namespace Agentic.Agents.Tooling;

public sealed class PatchConflictException : Exception
{
    public PatchConflictException(string message) : base(message) { }
}

/// <summary>Anchored find/replace that refuses to apply unless the anchor matches exactly once.</summary>
public static class TextPatch
{
    public static FileChange Replace(SandboxWorkspace ws, string path, params (string Find, string Replace)[] edits)
    {
        var original = ws.TryReadText(path) ?? throw new PatchConflictException($"{path} does not exist.");
        var usesCrlf = original.Contains("\r\n", StringComparison.Ordinal);
        var text = original.Replace("\r\n", "\n");

        foreach (var (rawFind, rawReplace) in edits)
        {
            var find = rawFind.Replace("\r\n", "\n");
            var first = text.IndexOf(find, StringComparison.Ordinal);
            if (first < 0)
                throw new PatchConflictException($"Anchor not found in {path}; the code has drifted from what the plan expected.");
            if (text.IndexOf(find, first + find.Length, StringComparison.Ordinal) >= 0)
                throw new PatchConflictException($"Anchor is ambiguous in {path} (multiple matches).");
            text = text[..first] + rawReplace.Replace("\r\n", "\n") + text[(first + find.Length)..];
        }

        return FileChange.Write(path, usesCrlf ? text.Replace("\n", "\r\n") : text);
    }
}
