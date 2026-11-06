namespace Agentic.Orchestration.Workflow;

public enum SdlcPhase
{
    Requirements,
    Analysis,
    Design,
    Planning,
    Implementation,
    Testing,
    Security,
    Documentation,
    Release,
}

/// <summary>How much damage a stage can do; drives approval policy.</summary>
public enum ImpactLevel
{
    Low,
    Medium,
    High,
}

public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialBackoff, double BackoffMultiplier = 2.0)
{
    public static RetryPolicy None { get; } = new(1, TimeSpan.Zero);

    public static RetryPolicy Default { get; } = new(3, TimeSpan.FromMilliseconds(200));

    public TimeSpan BackoffFor(int failedAttempts) =>
        TimeSpan.FromMilliseconds(InitialBackoff.TotalMilliseconds * Math.Pow(BackoffMultiplier, Math.Max(0, failedAttempts - 1)));
}

/// <summary>
/// A node in the SDLC dependency graph. Stages are declarative: the engine owns sequencing,
/// gating, retries, approvals and rollback; the agent only does the work.
/// </summary>
public sealed record StageDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required SdlcPhase Phase { get; init; }

    public required string AgentId { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

    /// <summary>Artifact keys this stage reads. Used for lineage and to detect stale results (re-plan).</summary>
    public IReadOnlyList<string> Consumes { get; init; } = Array.Empty<string>();

    /// <summary>Artifact keys this stage is expected to produce.</summary>
    public IReadOnlyList<string> Produces { get; init; } = Array.Empty<string>();

    public IReadOnlyList<IGate> EntryGates { get; init; } = Array.Empty<IGate>();

    public IReadOnlyList<IGate> ExitGates { get; init; } = Array.Empty<IGate>();

    public RetryPolicy Retry { get; init; } = RetryPolicy.Default;

    /// <summary>Alternative agent tried once after retries are exhausted.</summary>
    public string? FallbackAgentId { get; init; }

    public ImpactLevel Impact { get; init; } = ImpactLevel.Low;

    /// <summary>Force a human checkpoint regardless of impact (e.g. design sign-off, release).</summary>
    public bool RequiresApproval { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Set on stages created at runtime by a planner; the owner can replace them on re-plan.</summary>
    public string? GeneratedBy { get; init; }

    public string? Parameter(string key) => Parameters.TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// Runtime graph change requested by a planning agent (dynamic task expansion).
/// SyncStages are existing stages (e.g. build/test) that must wait for every added stage.
/// </summary>
public sealed record GraphPatch(IReadOnlyList<StageDefinition> AddStages, IReadOnlyList<string> SyncStages);
