namespace Agentic.Orchestration.Context;

public enum DecisionKind
{
    Interpretation,
    Assumption,
    Clarification,
    Design,
    Plan,
    Implementation,
    Retry,
    Fallback,
    Rollback,
    Replan,
    Policy,
    Approval,
    Release,
    SafeStop,
}

/// <summary>A recorded engineering decision with its rationale and the evidence it was based on.</summary>
public sealed record DecisionRecord(
    string Id,
    string StageId,
    int Attempt,
    string Actor,
    DecisionKind Kind,
    string Summary,
    string Rationale,
    IReadOnlyList<string> Alternatives,
    IReadOnlyList<ArtifactRef> BasedOn,
    DateTimeOffset At);

/// <summary>Draft created by an agent; the engine stamps identity, actor and time when it is committed.</summary>
public sealed record DecisionDraft(
    DecisionKind Kind,
    string Summary,
    string Rationale,
    IReadOnlyList<string>? Alternatives = null);

public sealed class DecisionLog
{
    private readonly object _gate = new();
    private readonly List<DecisionRecord> _records = new();
    private readonly TimeProvider _time;

    public DecisionLog(TimeProvider time) => _time = time;

    public DecisionRecord Record(
        string stageId, int attempt, string actor, DecisionDraft draft, IEnumerable<ArtifactRef>? basedOn = null)
    {
        lock (_gate)
        {
            var record = new DecisionRecord(
                $"D{_records.Count + 1:000}",
                stageId,
                attempt,
                actor,
                draft.Kind,
                draft.Summary,
                draft.Rationale,
                draft.Alternatives ?? Array.Empty<string>(),
                basedOn?.ToList() ?? new List<ArtifactRef>(),
                _time.GetUtcNow());
            _records.Add(record);
            return record;
        }
    }

    public IReadOnlyList<DecisionRecord> All()
    {
        lock (_gate)
            return _records.ToList();
    }
}
