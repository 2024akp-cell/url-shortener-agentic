using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentic.Orchestration.Observability;

namespace Agentic.Orchestration.Context;

/// <summary>Reference to an exact artifact version: the unit of lineage.</summary>
public sealed record ArtifactRef(string Key, int Version)
{
    public override string ToString() => $"{Key}@v{Version}";
}

/// <summary>An immutable, versioned output of a stage (or a human). Never overwritten, only superseded.</summary>
public sealed record Artifact(
    string Key,
    int Version,
    object Content,
    string ProducedBy,
    int Attempt,
    IReadOnlyList<ArtifactRef> DerivedFrom,
    DateTimeOffset CreatedAt,
    string ContentHash)
{
    public ArtifactRef Ref => new(Key, Version);

    public T As<T>() => Content is T typed
        ? typed
        : throw new InvalidOperationException($"Artifact {Ref} is {Content.GetType().Name}, not {typeof(T).Name}.");
}

/// <summary>
/// Shared, cross-stage memory. Every write creates a new version with its producer and the exact
/// input versions it was derived from, giving full decision lineage and enabling stale-result detection.
/// </summary>
public sealed class ContextStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Artifact>> _history = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ContextStore(TimeProvider time) => _time = time;

    public Artifact Put(string key, object content, string producedBy, int attempt, IEnumerable<ArtifactRef> derivedFrom)
    {
        lock (_gate)
        {
            if (!_history.TryGetValue(key, out var versions))
                _history[key] = versions = new List<Artifact>();

            var artifact = new Artifact(
                key,
                versions.Count + 1,
                content,
                producedBy,
                attempt,
                derivedFrom.ToList(),
                _time.GetUtcNow(),
                Hash(content));
            versions.Add(artifact);
            return artifact;
        }
    }

    public Artifact? Latest(string key)
    {
        lock (_gate)
            return _history.TryGetValue(key, out var versions) && versions.Count > 0 ? versions[^1] : null;
    }

    public T? Get<T>(string key) where T : class => Latest(key)?.Content as T;

    public T Require<T>(string key) where T : class =>
        Get<T>(key) ?? throw new InvalidOperationException($"Required artifact '{key}' is missing.");

    public int CurrentVersion(string key) => Latest(key)?.Version ?? 0;

    public IReadOnlyList<Artifact> History(string key)
    {
        lock (_gate)
            return _history.TryGetValue(key, out var versions) ? versions.ToList() : new List<Artifact>();
    }

    public IReadOnlyList<Artifact> All()
    {
        lock (_gate)
            return _history.Values.SelectMany(v => v).OrderBy(a => a.CreatedAt).ThenBy(a => a.Key).ToList();
    }

    public void Persist(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var artifact in All())
        {
            var baseName = $"{artifact.Key}.v{artifact.Version}";
            if (artifact.Content is string text)
                File.WriteAllText(Path.Combine(directory, baseName + ".md"), text);
            else
                File.WriteAllText(Path.Combine(directory, baseName + ".json"), JsonSerializer.Serialize(artifact.Content, Json.Options));

            var meta = new
            {
                artifact.Key,
                artifact.Version,
                artifact.ProducedBy,
                artifact.Attempt,
                DerivedFrom = artifact.DerivedFrom.Select(r => r.ToString()),
                artifact.CreatedAt,
                artifact.ContentHash,
            };
            File.WriteAllText(Path.Combine(directory, baseName + ".meta.json"), JsonSerializer.Serialize(meta, Json.Options));
        }
    }

    private static string Hash(object content)
    {
        var payload = content as string ?? JsonSerializer.Serialize(content, Json.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16].ToLowerInvariant();
    }
}
