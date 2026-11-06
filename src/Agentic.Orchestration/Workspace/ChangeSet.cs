namespace Agentic.Orchestration.Workspace;

/// <summary>A whole-file write (NewContent) or deletion (null). Paths are workspace-relative with '/' separators.</summary>
public sealed record FileChange(string Path, string? NewContent)
{
    public static FileChange Write(string path, string content) => new(path, content);

    public static FileChange Delete(string path) => new(path, null);
}

/// <summary>A proposed, reviewable unit of change. Agents propose; only the engine applies.</summary>
public sealed record ChangeSet(string Description, IReadOnlyList<FileChange> Changes)
{
    public IEnumerable<string> Paths => Changes.Select(c => c.Path);
}

/// <summary>Record of an applied change set, holding the prior content needed to undo it.</summary>
public sealed record AppliedChangeSet(
    int Sequence,
    string StageId,
    int Attempt,
    ChangeSet ChangeSet,
    IReadOnlyDictionary<string, string?> Before);
