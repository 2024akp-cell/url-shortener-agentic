namespace Agentic.Orchestration.Observability;

public sealed record StageTiming(string StageId, int Attempt, DateTimeOffset Start, DateTimeOffset End, string Outcome)
{
    public double DurationMs => (End - Start).TotalMilliseconds;
}

/// <summary>Reliability metrics for one run.</summary>
public sealed record RunMetrics
{
    public required string RunId { get; init; }
    public required string Scenario { get; init; }
    public required string Outcome { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public double EndToEndLatencyMs { get; init; }
    public double HumanWaitMs { get; init; }
    public int Attempts { get; init; }
    public int SuccessfulAttempts { get; init; }
    public double AttemptSuccessRate { get; init; }
    public int Retries { get; init; }
    public int Fallbacks { get; init; }
    public int Rollbacks { get; init; }
    public int FeedbackLoops { get; init; }
    public int Replans { get; init; }
    public int PolicyFindings { get; init; }
    public int PolicyDenials { get; init; }
    public int ApprovalsRequested { get; init; }
    public int ApprovalsRejected { get; init; }
    public int FailureEpisodes { get; init; }
    public int RecoveredEpisodes { get; init; }

    /// <summary>Mean time from a stage's first failure to its eventual success (recovered episodes only).</summary>
    public double? MttrMs { get; init; }

    public IReadOnlyList<StageTiming> Timeline { get; init; } = Array.Empty<StageTiming>();
}

/// <summary>Derives metrics purely from the event stream, so they always agree with the audit log.</summary>
public sealed class MetricsCollector : IRunObserver
{
    private readonly object _gate = new();
    private readonly List<RunEvent> _events = new();

    public void OnEvent(RunEvent runEvent)
    {
        lock (_gate)
            _events.Add(runEvent);
    }

    public RunMetrics Snapshot(string runId, string scenario)
    {
        List<RunEvent> events;
        lock (_gate)
            events = _events.ToList();

        int Count(string type) => events.Count(e => e.Type == type);

        var started = events.FirstOrDefault(e => e.Type == EventTypes.RunStarted)?.At ?? DateTimeOffset.MinValue;
        var finished = events.LastOrDefault(e => e.Type is EventTypes.RunCompleted or EventTypes.RunHalted);
        var outcome = finished?.Type == EventTypes.RunCompleted ? "succeeded" : finished is null ? "running" : "halted";

        var timeline = new List<StageTiming>();
        var open = new Dictionary<(string, int), DateTimeOffset>();
        foreach (var e in events)
        {
            if (e.StageId is null || e.Attempt is null)
                continue;
            var key = (e.StageId, e.Attempt.Value);
            if (e.Type == EventTypes.StageStarted)
                open[key] = e.At;
            else if (e.Type is EventTypes.AttemptSucceeded or EventTypes.AttemptFailed && open.Remove(key, out var start))
                timeline.Add(new StageTiming(e.StageId, e.Attempt.Value, start, e.At, e.Type == EventTypes.AttemptSucceeded ? "succeeded" : "failed"));
        }

        // Failure episodes: first failure of a stage opens an episode; its next success closes it.
        var episodeStart = new Dictionary<string, DateTimeOffset>();
        var recoveries = new List<double>();
        var episodes = 0;
        foreach (var e in events.Where(e => e.StageId is not null))
        {
            if (e.Type == EventTypes.AttemptFailed && episodeStart.TryAdd(e.StageId!, e.At))
                episodes++;
            else if (e.Type == EventTypes.StageSucceeded && episodeStart.Remove(e.StageId!, out var failedAt))
                recoveries.Add((e.At - failedAt).TotalMilliseconds);
        }

        var humanWait = 0.0;
        var pendingApprovals = new Dictionary<string, DateTimeOffset>();
        foreach (var e in events)
        {
            var key = e.StageId ?? "";
            if (e.Type is EventTypes.ApprovalRequested or EventTypes.ClarificationRequested)
                pendingApprovals[key] = e.At;
            else if (e.Type is EventTypes.ApprovalGranted or EventTypes.ApprovalRejected or EventTypes.ClarificationAnswered
                     && pendingApprovals.Remove(key, out var asked))
                humanWait += (e.At - asked).TotalMilliseconds;
        }

        var attempts = timeline.Count;
        var succeeded = timeline.Count(t => t.Outcome == "succeeded");
        return new RunMetrics
        {
            RunId = runId,
            Scenario = scenario,
            Outcome = outcome,
            StartedAt = started,
            EndToEndLatencyMs = finished is null ? 0 : (finished.At - started).TotalMilliseconds,
            HumanWaitMs = humanWait,
            Attempts = attempts,
            SuccessfulAttempts = succeeded,
            AttemptSuccessRate = attempts == 0 ? 0 : Math.Round((double)succeeded / attempts, 3),
            Retries = Count(EventTypes.Retry),
            Fallbacks = Count(EventTypes.Fallback),
            Rollbacks = Count(EventTypes.Rollback),
            FeedbackLoops = Count(EventTypes.FeedbackLoop),
            Replans = Count(EventTypes.Replan),
            PolicyFindings = Count(EventTypes.PolicyFinding),
            PolicyDenials = events.Count(e => e.Type == EventTypes.PolicyFinding && Equals(e.Data.GetValueOrDefault("verdict"), "Deny")),
            ApprovalsRequested = Count(EventTypes.ApprovalRequested),
            ApprovalsRejected = Count(EventTypes.ApprovalRejected),
            FailureEpisodes = episodes,
            RecoveredEpisodes = recoveries.Count,
            MttrMs = recoveries.Count == 0 ? null : Math.Round(recoveries.Average(), 1),
            Timeline = timeline,
        };
    }
}
