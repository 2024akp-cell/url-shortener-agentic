using System.Text;

namespace Agentic.Orchestration.Workspace;

/// <summary>
/// Isolated per-run copy of the target codebase. All mutations go through <see cref="Apply"/>, which
/// records prior content so any change set can be rolled back precisely. Paths are confined to the root.
/// </summary>
public sealed class SandboxWorkspace
{
    private static readonly string[] IgnoredDirectories = { "bin", "obj", ".vs", ".git", "TestResults" };

    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _originals = new(StringComparer.Ordinal);
    private readonly List<AppliedChangeSet> _applied = new();
    private int _sequence;

    public SandboxWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public IReadOnlyList<AppliedChangeSet> Applied
    {
        get
        {
            lock (_gate)
                return _applied.ToList();
        }
    }

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public string ReadText(string relativePath) => File.ReadAllText(Resolve(relativePath));

    public string? TryReadText(string relativePath) => Exists(relativePath) ? ReadText(relativePath) : null;

    public IReadOnlyList<string> ListFiles(string extension = "*")
    {
        if (!Directory.Exists(Root))
            return Array.Empty<string>();
        return Directory.EnumerateFiles(Root, extension == "*" ? "*" : "*" + extension, SearchOption.AllDirectories)
            .Select(ToRelative)
            .Where(p => !p.Split('/').Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Seeds the workspace from a source tree (used to set up brownfield scenarios). Not tracked as a change.</summary>
    public void SeedFrom(string sourceRoot, IEnumerable<string> relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var target = Resolve(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(sourceRoot, relative), target, overwrite: true);
        }
    }

    public AppliedChangeSet Apply(string stageId, int attempt, ChangeSet changeSet)
    {
        lock (_gate)
        {
            var before = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var change in changeSet.Changes)
            {
                var full = Resolve(change.Path);
                if (before.ContainsKey(change.Path))
                    throw new InvalidOperationException($"Change set touches '{change.Path}' more than once.");
                var prior = File.Exists(full) ? File.ReadAllText(full) : null;
                before[change.Path] = prior;
                _originals.TryAdd(change.Path, prior);
            }

            foreach (var change in changeSet.Changes)
                WriteOrDelete(change.Path, change.NewContent);

            var applied = new AppliedChangeSet(++_sequence, stageId, attempt, changeSet, before);
            _applied.Add(applied);
            return applied;
        }
    }

    public void Rollback(AppliedChangeSet applied)
    {
        lock (_gate)
        {
            foreach (var (path, content) in applied.Before)
                WriteOrDelete(path, content);
            _applied.Remove(applied);
        }
    }

    /// <summary>Files changed relative to the run's starting state, with their original and current content.</summary>
    public IReadOnlyList<(string Path, string? Original, string? Current)> Changes()
    {
        lock (_gate)
        {
            return _originals
                .Select(kv => (kv.Key, kv.Value, TryReadText(kv.Key)))
                .Where(t => t.Value != t.Item3)
                .OrderBy(t => t.Key, StringComparer.Ordinal)
                .ToList();
        }
    }

    public string UnifiedDiff()
    {
        var sb = new StringBuilder();
        foreach (var (path, original, current) in Changes())
            sb.Append(DiffBuilder.Unified(path, original, current));
        return sb.ToString();
    }

    private void WriteOrDelete(string relativePath, string? content)
    {
        var full = Resolve(relativePath);
        if (content is null)
        {
            if (File.Exists(full))
                File.Delete(full);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Resolves a relative path and refuses anything that escapes the sandbox root.</summary>
    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new UnauthorizedAccessException($"Path '{relativePath}' must be relative to the workspace.");
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path '{relativePath}' escapes the workspace sandbox.");
        return full;
    }

    private string ToRelative(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');
}
