namespace Agentic.Orchestration.Observability;

public static class EventTypes
{
    public const string RunStarted = "run.started";
    public const string RunCompleted = "run.completed";
    public const string RunHalted = "run.halted";
    public const string StageStarted = "stage.started";
    public const string StageSucceeded = "stage.succeeded";
    public const string StageFailed = "stage.failed";
    public const string StageSkipped = "stage.skipped";
    public const string StageInvalidated = "stage.invalidated";
    public const string GatePassed = "gate.passed";
    public const string GateFailed = "gate.failed";
    public const string AttemptSucceeded = "attempt.succeeded";
    public const string AttemptFailed = "attempt.failed";
    public const string Retry = "control.retry";
    public const string Fallback = "control.fallback";
    public const string Rollback = "control.rollback";
    public const string FeedbackLoop = "control.feedback-loop";
    public const string SafeStop = "control.safe-stop";
    public const string Replan = "control.replan";
    public const string GraphPatched = "graph.patched";
    public const string PolicyFinding = "policy.finding";
    public const string ChangeApplied = "workspace.change-applied";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalGranted = "approval.granted";
    public const string ApprovalRejected = "approval.rejected";
    public const string ClarificationRequested = "clarification.requested";
    public const string ClarificationAnswered = "clarification.answered";
    public const string ArtifactCommitted = "artifact.committed";
    public const string Decision = "decision.recorded";
    public const string AgentLog = "agent.log";
}

public sealed record RunEvent(
    string Type,
    string RunId,
    string? StageId,
    int? Attempt,
    string Actor,
    string Message,
    IReadOnlyDictionary<string, object?> Data,
    DateTimeOffset At);

public interface IRunObserver
{
    void OnEvent(RunEvent runEvent);
}

/// <summary>Fans events out to observers (audit log, metrics, console). Thread-safe.</summary>
public sealed class EventBus
{
    private readonly object _gate = new();
    private readonly List<IRunObserver> _observers = new();
    private readonly List<RunEvent> _events = new();

    public void Subscribe(IRunObserver observer)
    {
        lock (_gate)
            _observers.Add(observer);
    }

    public IReadOnlyList<RunEvent> Events
    {
        get
        {
            lock (_gate)
                return _events.ToList();
        }
    }

    public void Publish(RunEvent runEvent)
    {
        lock (_gate)
        {
            _events.Add(runEvent);
            foreach (var observer in _observers)
                observer.OnEvent(runEvent);
        }
    }
}
