using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Context;
using Agentic.Orchestration.Governance;
using Agentic.Orchestration.Observability;
using Agentic.Orchestration.Workflow;
using Agentic.Orchestration.Workspace;

namespace Agentic.Orchestration.Engine;

/// <summary>
/// Stateful, graph-driven SDLC orchestrator.
/// <para>
/// All state transitions happen on the single control loop; only agent work runs concurrently.
/// That keeps governance decisions (gates, policy, approvals, rollback, re-plan) serialised and
/// auditable while independent stages still execute in parallel.
/// </para>
/// </summary>
public sealed class Orchestrator
{
    private const string ClarificationsKey = "clarifications";

    private readonly AgentRegistry _agents;
    private readonly PolicyEngine _policies;
    private readonly IHumanGateway _human;
    private readonly GovernanceOptions _governance;

    public Orchestrator(AgentRegistry agents, PolicyEngine policies, IHumanGateway human, GovernanceOptions governance)
    {
        _agents = agents;
        _policies = policies;
        _human = human;
        _governance = governance;
    }

    public async Task<RunSession> RunAsync(RunSession session, CancellationToken ct = default)
    {
        var run = new RunState(session);
        using var stopAll = CancellationTokenSource.CreateLinkedTokenSource(ct);

        foreach (var stage in session.Graph.Stages)
            run.States[stage.Id] = new StageState();

        run.Emit(EventTypes.RunStarted, null, null, "orchestrator", $"Run started: {session.Graph.Stages.Count} stages",
            new() { ["stages"] = session.Graph.TopologicalOrder(), ["governance"] = DescribeGovernance() });

        var running = new Dictionary<Task<StageExecution>, string>();
        try
        {
            while (run.HaltReason is null)
            {
                run.HaltReason = CheckStopConditions(run, ct);
                if (run.HaltReason is not null)
                    break;

                foreach (var stageId in ReadyStages(run))
                {
                    if (running.Count >= _governance.MaxParallelism)
                        break;
                    var task = StartStage(run, stageId, stopAll.Token);
                    if (task is not null)
                        running[task] = stageId;
                    if (run.HaltReason is not null)
                        break;
                }
                if (run.HaltReason is not null)
                    break;

                if (running.Count == 0)
                {
                    var waiting = run.States.Values.Where(s => s.Status == StageStatus.Pending && s.NotBefore is not null).ToList();
                    if (waiting.Count > 0)
                    {
                        var until = waiting.Min(s => s.NotBefore!.Value);
                        var delay = until - session.Time.GetUtcNow();
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, ct);
                        continue;
                    }
                    if (run.States.Values.All(s => s.Status == StageStatus.Succeeded))
                        break;
                    run.HaltReason = run.States.FirstOrDefault(kv => kv.Value.Status == StageStatus.Failed) is { Key: not null } failed
                        ? $"Stage '{failed.Key}' failed: {failed.Value.LastError}"
                        : "No runnable stages remain (blocked dependency).";
                    break;
                }

                // Poll so the kill switch is honoured even while long stages run.
                var completed = await Task.WhenAny(running.Keys.Cast<Task>().Append(Task.Delay(500, ct)));
                if (completed is not Task<StageExecution> finished)
                    continue;
                running.Remove(finished);
                await ProcessAsync(run, await finished, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.HaltReason ??= "Run cancelled by operator (Ctrl+C).";
        }

        if (run.HaltReason is not null)
        {
            await stopAll.CancelAsync();
            await DrainAsync(running.Keys);
            SafeStop(run);
        }
        else
        {
            session.Succeeded = true;
            run.Emit(EventTypes.RunCompleted, null, null, "orchestrator", "All stages succeeded.", new());
        }

        session.HaltReason = run.HaltReason;
        session.Stages = session.Graph.Stages.Select(s =>
        {
            var st = run.States[s.Id];
            return new StageSnapshot(s.Id, s.Name, st.Status, st.Attempts, st.UsingFallback, st.Summary, st.LastError);
        }).ToList();
        return session;
    }

    private Dictionary<string, object?> DescribeGovernance() => new()
    {
        ["maxParallelism"] = _governance.MaxParallelism,
        ["maxTotalAttempts"] = _governance.MaxTotalAttempts,
        ["approvalThreshold"] = _governance.ApprovalThreshold.ToString(),
        ["policies"] = _policies.Policies.Select(p => p.Id).ToList(),
    };

    private string? CheckStopConditions(RunState run, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return "Run cancelled by operator (Ctrl+C).";
        if (_governance.KillSwitchPath is { } kill && File.Exists(kill))
            return $"Kill switch engaged ({kill}).";
        if (run.Session.Time.GetUtcNow() - run.StartedAt > _governance.MaxRunDuration)
            return $"Run exceeded max duration {_governance.MaxRunDuration}.";
        if (run.TotalAttempts >= _governance.MaxTotalAttempts && run.States.Values.Any(s => s.Status == StageStatus.Pending))
            return $"Attempt budget exhausted ({_governance.MaxTotalAttempts}).";
        return null;
    }

    private static IEnumerable<string> ReadyStages(RunState run)
    {
        var now = run.Session.Time.GetUtcNow();
        foreach (var id in run.Session.Graph.TopologicalOrder())
        {
            var st = run.States[id];
            if (st.Status != StageStatus.Pending || (st.NotBefore is { } notBefore && notBefore > now))
                continue;
            if (run.Session.Graph[id].DependsOn.All(d => run.States[d].Status == StageStatus.Succeeded))
                yield return id;
        }
    }

    private Task<StageExecution>? StartStage(RunState run, string stageId, CancellationToken stopAll)
    {
        var session = run.Session;
        var stage = session.Graph[stageId];
        var st = run.States[stageId];

        var gateContext = new GateContext(stage, session.Context, session.Workspace, new Dictionary<string, object>());
        foreach (var gate in stage.EntryGates)
        {
            var result = gate.Evaluate(gateContext);
            run.Emit(result.Passed ? EventTypes.GatePassed : EventTypes.GateFailed, stageId, st.Attempts + 1, "orchestrator",
                $"entry gate {gate.Name}: {result.Reason}", new() { ["gate"] = gate.Name, ["kind"] = "entry" });
            if (!result.Passed)
            {
                st.Status = StageStatus.Blocked;
                st.LastError = $"Entry gate '{gate.Name}' failed: {result.Reason}";
                run.HaltReason = st.LastError;
                return null;
            }
        }

        var attempt = ++st.Attempts;
        run.TotalAttempts++;
        st.Status = StageStatus.Running;
        st.NotBefore = null;
        st.DiscardOnCompletion = false;
        st.ConsumedVersions = stage.Consumes.ToDictionary(k => k, k => session.Context.CurrentVersion(k));

        var agentId = st.UsingFallback && stage.FallbackAgentId is not null ? stage.FallbackAgentId : stage.AgentId;
        var agent = _agents.Resolve(agentId);
        run.Emit(EventTypes.StageStarted, stageId, attempt, agentId,
            $"{stage.Name} (attempt {attempt}{(st.UsingFallback ? ", fallback" : "")})",
            new() { ["agent"] = agentId, ["phase"] = stage.Phase.ToString(), ["consumes"] = st.ConsumedVersions });

        var context = new AgentContext
        {
            RunId = session.RunId,
            Stage = stage,
            Attempt = attempt,
            Context = session.Context,
            Workspace = session.Workspace,
            Graph = session.Graph,
            Time = session.Time,
            Feedback = st.Feedback.ToList(),
            IsFallback = st.UsingFallback,
            Log = message => run.Emit(EventTypes.AgentLog, stageId, attempt, agentId, message, new()),
        };
        var injectFault = session.InjectedFaults.Contains((stageId, attempt));
        return Task.Run(() => ExecuteAgentAsync(agent, context, injectFault, stopAll), CancellationToken.None);
    }

    private static async Task<StageExecution> ExecuteAgentAsync(IAgent agent, AgentContext context, bool injectFault, CancellationToken stopAll)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopAll);
        timeout.CancelAfter(context.Stage.Timeout);
        try
        {
            if (injectFault)
                throw new IOException("Injected transient fault (chaos test).");
            var result = await agent.ExecuteAsync(context, timeout.Token);
            return new StageExecution(context.Stage.Id, context.Attempt, agent.Id, result, null);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stopAll.IsCancellationRequested)
        {
            return new StageExecution(context.Stage.Id, context.Attempt, agent.Id, null, $"Timed out after {context.Stage.Timeout}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StageExecution(context.Stage.Id, context.Attempt, agent.Id, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ProcessAsync(RunState run, StageExecution execution, CancellationToken ct)
    {
        var session = run.Session;
        if (!session.Graph.Contains(execution.StageId))
            return; // stage was removed by a re-plan while it ran
        var stage = session.Graph[execution.StageId];
        var st = run.States[stage.Id];
        var attempt = execution.Attempt;

        if (st.DiscardOnCompletion || IsStale(run, stage.Id))
        {
            st.Status = StageStatus.Pending;
            run.Emit(EventTypes.StageInvalidated, stage.Id, attempt, "orchestrator",
                "Result discarded: inputs changed while the stage was running.", new());
            return;
        }

        if (execution.Result is not { Success: true } result)
        {
            Fail(run, stage, attempt, execution.Error ?? execution.Result?.Error ?? "Agent reported failure.",
                execution.Result?.FeedbackTargets ?? new List<string>());
            return;
        }

        if (result.Clarifications.Count > 0)
        {
            await ClarifyAsync(run, stage, attempt, result, ct);
            return;
        }

        var findings = new List<PolicyFinding>();
        AppliedChangeSet? applied = null;
        if (result.Changes is { Changes.Count: > 0 } changes)
        {
            findings.AddRange(_policies.Evaluate(new PolicyContext(stage, changes, session.Workspace)));
            foreach (var finding in findings)
                run.Emit(EventTypes.PolicyFinding, stage.Id, attempt, finding.PolicyId, $"{finding.Verdict}: {finding.Message}",
                    new() { ["verdict"] = finding.Verdict.ToString(), ["category"] = finding.Category, ["path"] = finding.Path, ["line"] = finding.Line });

            var denials = findings.Where(f => f.Verdict == PolicyVerdict.Deny).ToList();
            if (denials.Count > 0)
            {
                var reason = "Policy denied change: " + string.Join("; ", denials.Select(d => $"[{d.PolicyId}] {d.Message} ({d.Path}:{d.Line})"));
                Record(run, stage.Id, attempt, "policy-engine", new DecisionDraft(DecisionKind.Policy,
                    $"Blocked change set '{changes.Description}'", reason));
                Fail(run, stage, attempt, reason, new List<string>());
                return;
            }

            applied = session.Workspace.Apply(stage.Id, attempt, changes);
            st.Applied.Add(applied);
            run.Emit(EventTypes.ChangeApplied, stage.Id, attempt, execution.AgentId, changes.Description,
                new() { ["files"] = changes.Paths.ToList(), ["sequence"] = applied.Sequence });
        }

        var gateContext = new GateContext(stage, session.Context, session.Workspace, result.Outputs);
        foreach (var gate in stage.ExitGates)
        {
            var gateResult = gate.Evaluate(gateContext);
            run.Emit(gateResult.Passed ? EventTypes.GatePassed : EventTypes.GateFailed, stage.Id, attempt, "orchestrator",
                $"exit gate {gate.Name}: {gateResult.Reason}", new() { ["gate"] = gate.Name, ["kind"] = "exit" });
            if (!gateResult.Passed)
            {
                if (applied is not null)
                    RollbackStageChanges(run, stage.Id, attempt, "exit gate failed", applied);
                Fail(run, stage, attempt, $"Exit gate '{gate.Name}' failed: {gateResult.Reason}", result.FeedbackTargets);
                return;
            }
        }

        ApprovalDecision? approval = null;
        var approvalReason = ApprovalReason(stage, findings);
        if (approvalReason is not null)
        {
            approval = await RequestApprovalAsync(run, stage, attempt, approvalReason, result, findings, applied, ct);
            if (approval.Outcome == ApprovalOutcome.Rejected)
            {
                if (applied is not null)
                    RollbackStageChanges(run, stage.Id, attempt, "approval rejected", applied);
                st.Status = StageStatus.Failed;
                st.LastError = $"Rejected by {approval.Approver}: {approval.Comment}";
                run.Emit(EventTypes.StageFailed, stage.Id, attempt, approval.Approver, st.LastError, new());
                run.HaltReason = st.LastError;
                return;
            }
        }

        Commit(run, stage, attempt, execution.AgentId, result);

        if (approval is { Outcome: ApprovalOutcome.ApprovedWithChanges, Amendments: { Count: > 0 } amendments })
        {
            foreach (var (key, content) in amendments)
            {
                var artifact = session.Context.Put(key, content, $"human:{approval.Approver}", 0, Array.Empty<ArtifactRef>());
                run.Emit(EventTypes.ArtifactCommitted, stage.Id, attempt, $"human:{approval.Approver}",
                    $"Amended {artifact.Ref} during approval", new() { ["artifact"] = artifact.Ref.ToString(), ["hash"] = artifact.ContentHash });
            }
            Record(run, stage.Id, attempt, $"human:{approval.Approver}", new DecisionDraft(DecisionKind.Replan,
                $"Approved with changes to: {string.Join(", ", amendments.Keys)}",
                approval.Comment));
        }

        InvalidateStaleStages(run, stage.Id);
    }

    private void Commit(RunState run, StageDefinition stage, int attempt, string agentId, AgentResult result)
    {
        var session = run.Session;
        var st = run.States[stage.Id];
        var consumed = st.ConsumedVersions.Where(kv => kv.Value > 0).Select(kv => new ArtifactRef(kv.Key, kv.Value)).ToList();

        foreach (var (key, content) in result.Outputs)
        {
            var artifact = session.Context.Put(key, content, stage.Id, attempt, consumed);
            run.Emit(EventTypes.ArtifactCommitted, stage.Id, attempt, agentId, $"Committed {artifact.Ref}",
                new() { ["artifact"] = artifact.Ref.ToString(), ["derivedFrom"] = consumed.Select(c => c.ToString()).ToList(), ["hash"] = artifact.ContentHash });
        }

        foreach (var draft in result.Decisions)
            Record(run, stage.Id, attempt, agentId, draft, consumed);

        if (result.GraphPatch is { } patch)
            ApplyGraphPatch(run, stage, attempt, patch);

        st.Status = StageStatus.Succeeded;
        st.Summary = result.Summary;
        run.Emit(EventTypes.AttemptSucceeded, stage.Id, attempt, agentId, result.Summary, new());
        run.Emit(EventTypes.StageSucceeded, stage.Id, attempt, agentId, $"{stage.Name} succeeded", new());
    }

    private void ApplyGraphPatch(RunState run, StageDefinition owner, int attempt, GraphPatch patch)
    {
        var session = run.Session;
        var previous = session.Graph.Stages.Where(s => s.GeneratedBy == owner.Id).Select(s => s.Id).ToList();
        foreach (var id in previous)
            RollbackAllChangesOf(run, id, "stage replaced by re-plan");

        var removed = session.Graph.ApplyPatch(owner.Id, patch);
        foreach (var id in removed)
            run.States.Remove(id);
        foreach (var stage in patch.AddStages)
            run.States[stage.Id] = new StageState();
        foreach (var syncId in patch.SyncStages)
            if (run.States[syncId].Status == StageStatus.Succeeded)
                ResetToPending(run, syncId);

        run.Emit(EventTypes.GraphPatched, owner.Id, attempt, owner.AgentId,
            $"Expanded graph with {patch.AddStages.Count} stage(s){(removed.Count > 0 ? $", replaced {removed.Count}" : "")}",
            new()
            {
                ["added"] = patch.AddStages.Select(s => new { s.Id, s.Name, s.DependsOn }).ToList(),
                ["removed"] = removed,
                ["sync"] = patch.SyncStages,
            });
        if (removed.Count > 0)
            run.Emit(EventTypes.Replan, owner.Id, attempt, "orchestrator", $"Re-planned: replaced {string.Join(", ", removed)}", new());
    }

    private async Task ClarifyAsync(RunState run, StageDefinition stage, int attempt, AgentResult result, CancellationToken ct)
    {
        var session = run.Session;
        var st = run.States[stage.Id];
        if (++st.ClarificationRounds > _governance.MaxClarificationRounds)
        {
            Fail(run, stage, attempt, "Ambiguity unresolved after maximum clarification rounds.", new List<string>(), retryable: false);
            return;
        }

        run.Emit(EventTypes.ClarificationRequested, stage.Id, attempt, stage.AgentId,
            $"{result.Clarifications.Count} clarification(s) needed",
            new() { ["questions"] = result.Clarifications });
        var answers = await _human.RequestClarificationAsync(session.RunId, stage.Id, result.Clarifications, ct);

        var unanswered = result.Clarifications.Where(q => !answers.Answers.ContainsKey(q.Id)).ToList();
        if (unanswered.Count > 0)
        {
            st.Status = StageStatus.Blocked;
            st.LastError = $"Clarification unanswered: {string.Join(", ", unanswered.Select(q => q.Id))}";
            run.HaltReason = st.LastError;
            return;
        }

        var merged = new Dictionary<string, string>(session.Context.Get<Dictionary<string, string>>(ClarificationsKey) ?? new());
        foreach (var (id, answer) in answers.Answers)
            merged[id] = answer;
        var artifact = session.Context.Put(ClarificationsKey, merged, $"human:{answers.Respondent}", 0, Array.Empty<ArtifactRef>());
        foreach (var question in result.Clarifications)
            Record(run, stage.Id, attempt, $"human:{answers.Respondent}",
                new DecisionDraft(DecisionKind.Clarification, $"{question.Id}: {answers.Answers[question.Id]}", question.Question),
                new[] { artifact.Ref });

        run.Emit(EventTypes.ClarificationAnswered, stage.Id, attempt, $"human:{answers.Respondent}",
            $"Answers recorded as {artifact.Ref}", new() { ["answers"] = answers.Answers });
        st.Status = StageStatus.Pending; // re-run with answers; not counted as a failure
    }

    private async Task<ApprovalDecision> RequestApprovalAsync(
        RunState run, StageDefinition stage, int attempt, string reason, AgentResult result,
        IReadOnlyList<PolicyFinding> findings, AppliedChangeSet? applied, CancellationToken ct)
    {
        var session = run.Session;
        var diff = applied is null
            ? null
            : string.Concat(applied.Before.Select(kv => DiffBuilder.Unified(kv.Key, kv.Value, session.Workspace.TryReadText(kv.Key))));
        var request = new ApprovalRequest(session.RunId, stage.Id, stage.Name, attempt, reason, result.Summary, findings, diff, result.Outputs);

        run.Emit(EventTypes.ApprovalRequested, stage.Id, attempt, "orchestrator", reason,
            new() { ["findings"] = findings.Count, ["filesChanged"] = applied?.Before.Count ?? 0 });
        var decision = await _human.RequestApprovalAsync(request, ct);

        var granted = decision.Outcome != ApprovalOutcome.Rejected;
        run.Emit(granted ? EventTypes.ApprovalGranted : EventTypes.ApprovalRejected, stage.Id, attempt, $"human:{decision.Approver}",
            $"{decision.Outcome}: {decision.Comment}", new() { ["outcome"] = decision.Outcome.ToString() });
        Record(run, stage.Id, attempt, $"human:{decision.Approver}",
            new DecisionDraft(DecisionKind.Approval, $"{decision.Outcome} — {stage.Name}", $"{reason}. Reviewer: {decision.Comment}"));
        return decision;
    }

    private string? ApprovalReason(StageDefinition stage, IReadOnlyList<PolicyFinding> findings)
    {
        var reasons = new List<string>();
        if (stage.RequiresApproval)
            reasons.Add("mandatory checkpoint");
        if (stage.Impact >= _governance.ApprovalThreshold)
            reasons.Add($"impact {stage.Impact}");
        reasons.AddRange(findings.Where(f => f.Verdict == PolicyVerdict.RequireApproval)
            .Select(f => $"{f.PolicyId}: {f.Message}{(f.Path is null ? "" : $" ({f.Path})")}"));
        return reasons.Count == 0 ? null : string.Join("; ", reasons.Distinct());
    }

    private void Fail(RunState run, StageDefinition stage, int attempt, string reason, IReadOnlyList<string> feedbackTargets, bool retryable = true)
    {
        var st = run.States[stage.Id];
        st.Status = StageStatus.Pending; // no longer running; branches below decide the final state
        st.LastError = reason;
        st.Feedback.Add(new AttemptFeedback(attempt, stage.Id, reason));
        run.Emit(EventTypes.AttemptFailed, stage.Id, attempt, st.UsingFallback ? stage.FallbackAgentId ?? stage.AgentId : stage.AgentId, reason, new());

        // Non-linear recovery: re-open the upstream stages that caused a downstream failure.
        var targets = feedbackTargets.Where(t => run.Session.Graph.Contains(t) && t != stage.Id).Distinct().ToList();
        if (targets.Count > 0 && run.FeedbackLoops < _governance.MaxFeedbackLoops)
        {
            run.FeedbackLoops++;
            foreach (var target in targets)
                run.States[target].Feedback.Add(new AttemptFeedback(attempt, stage.Id, reason));
            run.Emit(EventTypes.FeedbackLoop, stage.Id, attempt, "orchestrator",
                $"Routing failure back to {string.Join(", ", targets)} (loop {run.FeedbackLoops}/{_governance.MaxFeedbackLoops})",
                new() { ["targets"] = targets });
            Record(run, stage.Id, attempt, "orchestrator", new DecisionDraft(DecisionKind.Retry,
                $"Re-open {string.Join(", ", targets)} with failure feedback",
                $"{stage.Name} failed and attributed the failure to upstream work: {reason}",
                new[] { $"Retry {stage.Name} unchanged (would fail identically)", "Halt the run" }));
            Invalidate(run, targets.Concat(targets.SelectMany(t => run.Session.Graph.TransitiveDependents(t))).Append(stage.Id), "feedback loop");
            return;
        }

        st.Failures++;
        if (retryable && st.Failures < stage.Retry.MaxAttempts)
        {
            var backoff = stage.Retry.BackoffFor(st.Failures);
            st.Status = StageStatus.Pending;
            st.NotBefore = run.Session.Time.GetUtcNow() + backoff;
            run.Emit(EventTypes.Retry, stage.Id, attempt, "orchestrator",
                $"Retry {st.Failures}/{stage.Retry.MaxAttempts - 1} after {backoff.TotalMilliseconds:0} ms", new() { ["backoffMs"] = backoff.TotalMilliseconds });
            return;
        }

        if (retryable && stage.FallbackAgentId is not null && !st.UsingFallback)
        {
            st.UsingFallback = true;
            st.Failures = 0;
            st.Status = StageStatus.Pending;
            run.Emit(EventTypes.Fallback, stage.Id, attempt, "orchestrator", $"Switching to fallback agent '{stage.FallbackAgentId}'",
                new() { ["fallbackAgent"] = stage.FallbackAgentId });
            Record(run, stage.Id, attempt, "orchestrator", new DecisionDraft(DecisionKind.Fallback,
                $"Use fallback agent {stage.FallbackAgentId}", $"Primary agent exhausted {stage.Retry.MaxAttempts} attempt(s): {reason}"));
            return;
        }

        st.Status = StageStatus.Failed;
        run.Emit(EventTypes.StageFailed, stage.Id, attempt, "orchestrator", reason, new());
        run.HaltReason = $"Stage '{stage.Id}' failed after {st.Attempts} attempt(s): {reason}";
    }

    /// <summary>Re-plan: any completed stage whose consumed inputs have a newer version is re-run, with its dependents.</summary>
    private void InvalidateStaleStages(RunState run, string triggeredBy)
    {
        var stale = run.States.Where(kv => kv.Value.Status is StageStatus.Succeeded or StageStatus.Running && IsStale(run, kv.Key))
            .Select(kv => kv.Key).ToList();
        if (stale.Count == 0)
            return;

        if (++run.Replans > _governance.MaxReplans)
        {
            run.HaltReason = $"Re-plan limit ({_governance.MaxReplans}) exceeded; upstream outputs keep changing.";
            return;
        }

        var affected = stale.Concat(stale.SelectMany(s => run.Session.Graph.TransitiveDependents(s))).Distinct().ToList();
        run.Emit(EventTypes.Replan, triggeredBy, null, "orchestrator",
            $"Upstream change detected; re-planning {string.Join(", ", affected)}",
            new() { ["stale"] = stale, ["affected"] = affected });
        Record(run, triggeredBy, 0, "orchestrator", new DecisionDraft(DecisionKind.Replan,
            $"Invalidate {affected.Count} stage(s) after upstream change",
            $"Stages {string.Join(", ", stale)} consumed artifact versions that are no longer current."));
        Invalidate(run, affected, "upstream change");
    }

    private void Invalidate(RunState run, IEnumerable<string> stageIds, string reason)
    {
        var ids = stageIds.Where(run.States.ContainsKey).Distinct().ToList();

        // Undo workspace changes newest-first so overlapping edits unwind correctly.
        var toUndo = run.Session.Workspace.Applied.Where(a => ids.Contains(a.StageId)).OrderByDescending(a => a.Sequence).ToList();
        foreach (var applied in toUndo)
            RollbackStageChanges(run, applied.StageId, applied.Attempt, reason, applied);

        foreach (var id in ids)
        {
            var st = run.States[id];
            if (st.Status == StageStatus.Running)
                st.DiscardOnCompletion = true;
            else if (st.Status is StageStatus.Succeeded or StageStatus.Pending)
                ResetToPending(run, id);
            run.Emit(EventTypes.StageInvalidated, id, null, "orchestrator", $"Invalidated ({reason})", new());
        }
    }

    private static void ResetToPending(RunState run, string stageId)
    {
        var st = run.States[stageId];
        st.Status = StageStatus.Pending;
        st.Failures = 0;
        st.UsingFallback = false;
        st.NotBefore = null;
    }

    private static bool IsStale(RunState run, string stageId)
    {
        var stage = run.Session.Graph[stageId];
        var consumed = run.States[stageId].ConsumedVersions;
        return stage.Consumes.Any(k => run.Session.Context.CurrentVersion(k) > consumed.GetValueOrDefault(k));
    }

    private void RollbackAllChangesOf(RunState run, string stageId, string reason)
    {
        foreach (var applied in run.Session.Workspace.Applied.Where(a => a.StageId == stageId).OrderByDescending(a => a.Sequence).ToList())
            RollbackStageChanges(run, stageId, applied.Attempt, reason, applied);
    }

    private static void RollbackStageChanges(RunState run, string stageId, int attempt, string reason, AppliedChangeSet applied)
    {
        run.Session.Workspace.Rollback(applied);
        if (run.States.TryGetValue(stageId, out var st))
            st.Applied.Remove(applied);
        run.Emit(EventTypes.Rollback, stageId, attempt, "orchestrator",
            $"Rolled back change set #{applied.Sequence} ({reason})", new() { ["files"] = applied.Before.Keys.ToList() });
    }

    private void SafeStop(RunState run)
    {
        foreach (var (id, st) in run.States)
            if (st.Status is StageStatus.Pending or StageStatus.Running)
            {
                st.Status = StageStatus.Skipped;
                run.Emit(EventTypes.StageSkipped, id, null, "orchestrator", "Skipped due to safe-stop", new());
            }

        if (_governance.RollbackWorkspaceOnHalt)
            foreach (var applied in run.Session.Workspace.Applied.OrderByDescending(a => a.Sequence).ToList())
                RollbackStageChanges(run, applied.StageId, applied.Attempt, "safe-stop full rollback", applied);

        Record(run, "orchestrator", 0, "orchestrator", new DecisionDraft(DecisionKind.SafeStop, "Run halted", run.HaltReason!));
        run.Emit(EventTypes.SafeStop, null, null, "orchestrator", run.HaltReason!, new() { ["rolledBack"] = _governance.RollbackWorkspaceOnHalt });
        run.Emit(EventTypes.RunHalted, null, null, "orchestrator", run.HaltReason!, new());
    }

    private static async Task DrainAsync(IEnumerable<Task<StageExecution>> running)
    {
        try
        {
            await Task.WhenAll(running);
        }
        catch
        {
            // Results of in-flight stages are discarded on safe-stop.
        }
    }

    private static void Record(RunState run, string stageId, int attempt, string actor, DecisionDraft draft, IEnumerable<ArtifactRef>? basedOn = null)
    {
        var record = run.Session.Decisions.Record(stageId, attempt, actor, draft, basedOn);
        run.Emit(EventTypes.Decision, stageId == "orchestrator" ? null : stageId, attempt == 0 ? null : attempt, actor,
            $"[{record.Kind}] {record.Summary}", new() { ["decisionId"] = record.Id, ["basedOn"] = record.BasedOn.Select(b => b.ToString()).ToList() });
    }

    private sealed record StageExecution(string StageId, int Attempt, string AgentId, AgentResult? Result, string? Error);

    private sealed class StageState
    {
        public StageStatus Status { get; set; } = StageStatus.Pending;
        public int Attempts { get; set; }
        public int Failures { get; set; }
        public bool UsingFallback { get; set; }
        public bool DiscardOnCompletion { get; set; }
        public int ClarificationRounds { get; set; }
        public DateTimeOffset? NotBefore { get; set; }
        public string? LastError { get; set; }
        public string? Summary { get; set; }
        public List<AttemptFeedback> Feedback { get; } = new();
        public List<AppliedChangeSet> Applied { get; } = new();
        public Dictionary<string, int> ConsumedVersions { get; set; } = new();
    }

    private sealed class RunState
    {
        public RunState(RunSession session)
        {
            Session = session;
            StartedAt = session.Time.GetUtcNow();
        }

        public RunSession Session { get; }
        public DateTimeOffset StartedAt { get; }
        public Dictionary<string, StageState> States { get; } = new(StringComparer.Ordinal);
        public int TotalAttempts { get; set; }
        public int FeedbackLoops { get; set; }
        public int Replans { get; set; }
        public string? HaltReason { get; set; }

        public void Emit(string type, string? stageId, int? attempt, string actor, string message, Dictionary<string, object?> data) =>
            Session.Bus.Publish(new RunEvent(type, Session.RunId, stageId, attempt, actor, message, data, Session.Time.GetUtcNow()));
    }
}
