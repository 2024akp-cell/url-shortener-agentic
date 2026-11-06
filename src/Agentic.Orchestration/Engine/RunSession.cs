using Agentic.Orchestration.Context;
using Agentic.Orchestration.Observability;
using Agentic.Orchestration.Workflow;
using Agentic.Orchestration.Workspace;

namespace Agentic.Orchestration.Engine;

public enum StageStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Blocked,
}

public sealed record StageSnapshot(
    string StageId,
    string Name,
    StageStatus Status,
    int Attempts,
    bool UsedFallback,
    string? Summary,
    string? LastError);

/// <summary>Everything that belongs to one execution: graph, shared context, lineage, workspace and telemetry.</summary>
public sealed class RunSession
{
    public RunSession(string runId, string scenario, string runDirectory, WorkflowGraph graph, SandboxWorkspace workspace, TimeProvider time)
    {
        RunId = runId;
        Scenario = scenario;
        RunDirectory = runDirectory;
        Graph = graph;
        Workspace = workspace;
        Time = time;
        Context = new ContextStore(time);
        Decisions = new DecisionLog(time);
        Bus.Subscribe(Metrics);
    }

    public string RunId { get; }

    public string Scenario { get; }

    public string RunDirectory { get; }

    public WorkflowGraph Graph { get; }

    public SandboxWorkspace Workspace { get; }

    public TimeProvider Time { get; }

    public ContextStore Context { get; }

    public DecisionLog Decisions { get; }

    public EventBus Bus { get; } = new();

    public MetricsCollector Metrics { get; } = new();

    /// <summary>Chaos testing: (stageId, attempt) pairs that will fail with a simulated transient fault.</summary>
    public HashSet<(string StageId, int Attempt)> InjectedFaults { get; } = new();

    public string? HaltReason { get; internal set; }

    public bool Succeeded { get; internal set; }

    public IReadOnlyList<StageSnapshot> Stages { get; internal set; } = Array.Empty<StageSnapshot>();

    public RunMetrics MetricsSnapshot() => Metrics.Snapshot(RunId, Scenario);
}
