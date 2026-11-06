using Agentic.Agents.Model;
using Agentic.Agents.Reasoning;
using Agentic.Agents.Stages;
using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Context;
using Agentic.Orchestration.Engine;
using Agentic.Orchestration.Governance;
using Agentic.Orchestration.Observability;
using Agentic.Orchestration.Workflow;
using static Agentic.Orchestration.Tests.Harness;

namespace Agentic.Orchestration.Tests;

public class WorkflowGraphTests
{
    [Fact]
    public void Create_RejectsCycles() =>
        Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Create("g", new[] { Stage("a", "x", "b"), Stage("b", "x", "a") }));

    [Fact]
    public void Create_RejectsUnknownDependency() =>
        Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Create("g", new[] { Stage("a", "x", "missing") }));

    [Fact]
    public void Levels_GroupIndependentStagesForParallelism()
    {
        var graph = WorkflowGraph.Create("g", new[] { Stage("a", "x"), Stage("b", "x", "a"), Stage("c", "x", "a"), Stage("d", "x", "b", "c") });

        Assert.Equal(new[] { "b", "c" }, graph.Levels()[1]);
    }

    [Fact]
    public void ApplyPatch_ThatWouldCreateCycle_LeavesGraphUnchanged()
    {
        var graph = WorkflowGraph.Create("g", new[] { Stage("plan", "x"), Stage("test", "x", "plan") });

        Assert.Throws<WorkflowValidationException>(() =>
            graph.ApplyPatch("plan", new GraphPatch(new[] { Stage("impl", "x", "test") }, new[] { "test" })));
        Assert.Equal(2, graph.Stages.Count);
    }
}

public class OrchestratorTests
{
    [Fact]
    public async Task IndependentStages_RunInParallel_AndSyncBeforeDependent()
    {
        var running = 0;
        var peak = 0;
        var slow = new DelegateAgent("slow", async _ =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref running));
            await Task.Delay(300);
            Interlocked.Decrement(ref running);
            return Ok();
        });
        var session = Session(Stage("root", "ok"), Stage("left", "slow", "root"), Stage("right", "slow", "root"), Stage("join", "ok", "left", "right"));

        await RunAsync(session, new IAgent[] { new DelegateAgent("ok", _ => Ok()), slow });

        Assert.True(session.Succeeded);
        Assert.Equal(2, peak);
        var timeline = session.MetricsSnapshot().Timeline;
        Assert.True(timeline.Single(t => t.StageId == "join").Start >= timeline.Where(t => t.StageId is "left" or "right").Max(t => t.End));
    }

    [Fact]
    public async Task TransientFailure_IsRetried_AndMttrIsRecorded()
    {
        var flaky = new DelegateAgent("flaky", ctx => ctx.Attempt == 1 ? AgentResult.Failed("transient") : Ok());
        var session = Session(Stage("s", "flaky") with { Retry = new RetryPolicy(3, TimeSpan.FromMilliseconds(10)) });

        await RunAsync(session, new IAgent[] { flaky });

        var metrics = session.MetricsSnapshot();
        Assert.True(session.Succeeded);
        Assert.Equal(1, metrics.Retries);
        Assert.Equal(1, metrics.RecoveredEpisodes);
        Assert.NotNull(metrics.MttrMs);
    }

    [Fact]
    public async Task ExhaustedRetries_SwitchToFallbackAgent()
    {
        var session = Session(Stage("s", "primary") with { FallbackAgentId = "fallback" });

        await RunAsync(session, new IAgent[] { new DelegateAgent("primary", _ => AgentResult.Failed("nope")), new DelegateAgent("fallback", _ => Ok()) });

        Assert.True(session.Succeeded);
        Assert.True(session.Stages.Single().UsedFallback);
        Assert.Equal(1, session.MetricsSnapshot().Fallbacks);
    }

    [Fact]
    public async Task UnrecoverableFailure_SafeStops_AndSkipsDownstream()
    {
        var downstream = new DelegateAgent("down", _ => Ok());
        var session = Session(Stage("a", "fail"), Stage("b", "down", "a"));

        await RunAsync(session, new IAgent[] { new DelegateAgent("fail", _ => AgentResult.Failed("broken")), downstream });

        Assert.False(session.Succeeded);
        Assert.Equal(StageStatus.Failed, StatusOf(session, "a"));
        Assert.Equal(StageStatus.Skipped, StatusOf(session, "b"));
        Assert.Equal(0, downstream.Calls);
        Assert.Contains(session.Bus.Events, e => e.Type == EventTypes.SafeStop);
    }

    [Fact]
    public async Task PolicyDenial_BlocksChange_BeforeItTouchesWorkspace()
    {
        var session = Session(Stage("s", "leaky"));

        await RunAsync(session, new IAgent[] { new DelegateAgent("leaky", _ => Write("src/Config.cs", "var apiKey = \"sk-abcdefghijklmnopqrstuvwxyz123\";")) });

        Assert.False(session.Succeeded);
        Assert.False(session.Workspace.Exists("src/Config.cs"));
        Assert.Contains(session.Bus.Events, e => e.Type == EventTypes.PolicyFinding && e.Actor == "SEC-001-secrets");
    }

    [Fact]
    public async Task WriteOutsideScope_IsDenied()
    {
        var session = Session(Stage("s", "escape"));

        await RunAsync(session, new IAgent[] { new DelegateAgent("escape", _ => Write(".github/workflows/deploy.yml", "on: push")) });

        Assert.False(session.Succeeded);
        Assert.Contains(session.Bus.Events, e => e.Type == EventTypes.PolicyFinding && e.Actor == "AUT-001-write-scope");
    }

    [Fact]
    public async Task RejectedApproval_RollsBackChange_AndHalts()
    {
        var session = Session(Stage("s", "writer") with { RequiresApproval = true });
        var human = new ScriptedHumanGateway(rule: (_, _) => ApprovalDecision.Reject("x", "not this"));

        await RunAsync(session, new IAgent[] { new DelegateAgent("writer", _ => Write("src/A.cs", "class A {}")) }, human);

        Assert.False(session.Succeeded);
        Assert.False(session.Workspace.Exists("src/A.cs"));
        Assert.Equal(1, session.MetricsSnapshot().Rollbacks);
        Assert.Contains("Rejected", session.HaltReason);
    }

    [Fact]
    public async Task HighImpactStage_RequiresApproval_UnattendedRunDeniesIt()
    {
        var session = Session(Stage("release", "ok") with { Impact = ImpactLevel.High });

        await RunAsync(session, new IAgent[] { new DelegateAgent("ok", _ => Ok()) }, new DenyAllHumanGateway());

        Assert.False(session.Succeeded);
        Assert.Equal(StageStatus.Failed, StatusOf(session, "release"));
    }

    [Fact]
    public async Task DownstreamFailure_ReopensUpstreamStage_WithFeedback()
    {
        var impl = new DelegateAgent("impl", ctx => Write("src/Fix.cs", ctx.Feedback.Count == 0 ? "bad" : "good"));
        var test = new DelegateAgent("test", ctx => ctx.Workspace.ReadText("src/Fix.cs") == "good"
            ? Ok()
            : new AgentResult { Success = false, Error = "assertion failed", FeedbackTargets = { "impl" } });
        var session = Session(Stage("impl", "impl"), Stage("test", "test", "impl"));

        await RunAsync(session, new IAgent[] { impl, test });

        Assert.True(session.Succeeded);
        Assert.Equal(2, impl.Calls);
        Assert.Equal("good", session.Workspace.ReadText("src/Fix.cs"));
        var metrics = session.MetricsSnapshot();
        Assert.Equal(1, metrics.FeedbackLoops);
        Assert.Equal(1, metrics.Rollbacks);
    }

    [Fact]
    public async Task ApprovedWithChanges_InvalidatesStaleConsumers_AndReplans()
    {
        var reader = new DelegateAgent("reader", ctx => new AgentResult { Outputs = { ["derived"] = "from " + ctx.Context.Require<string>("input") } });
        var session = Session(
            Stage("read", "reader") with { Consumes = new[] { "input" } },
            Stage("gate", "ok", "read") with { RequiresApproval = true });
        session.Context.Put("input", "v1", "human", 0, Array.Empty<ArtifactRef>());
        var human = new ScriptedHumanGateway(rule: (_, nth) => nth == 0
            ? new ApprovalDecision(ApprovalOutcome.ApprovedWithChanges, "x", "change input", new Dictionary<string, object> { ["input"] = "v2" })
            : null);

        await RunAsync(session, new IAgent[] { reader, new DelegateAgent("ok", _ => Ok()) }, human);

        Assert.True(session.Succeeded);
        Assert.Equal(2, reader.Calls);
        Assert.Equal("from v2", session.Context.Require<string>("derived"));
        Assert.Equal(new[] { "input@v1" }, session.Context.History("derived")[0].DerivedFrom.Select(r => r.ToString()));
        Assert.Equal(new[] { "input@v2" }, session.Context.Latest("derived")!.DerivedFrom.Select(r => r.ToString()));
        Assert.True(session.MetricsSnapshot().Replans >= 1);
    }

    [Fact]
    public async Task KillSwitch_StopsRunAtNextSafePoint()
    {
        var killFile = Path.Combine(Path.GetTempPath(), $"agentic-stop-{Guid.NewGuid():N}");
        var session = Session(Stage("a", "trip"), Stage("b", "ok", "a"));
        var trip = new DelegateAgent("trip", _ =>
        {
            File.WriteAllText(killFile, "stop");
            return Ok();
        });

        await RunAsync(session, new IAgent[] { trip, new DelegateAgent("ok", _ => Ok()) }, governance: new GovernanceOptions { KillSwitchPath = killFile });
        File.Delete(killFile);

        Assert.False(session.Succeeded);
        Assert.Contains("Kill switch", session.HaltReason);
        Assert.Equal(StageStatus.Skipped, StatusOf(session, "b"));
    }

    [Fact]
    public async Task AttemptBudget_PreventsRunawayLoops()
    {
        var session = Session(Stage("s", "fail") with { Retry = new RetryPolicy(100, TimeSpan.Zero) });

        await RunAsync(session, new IAgent[] { new DelegateAgent("fail", _ => AgentResult.Failed("again")) }, governance: new GovernanceOptions { MaxTotalAttempts = 5 });

        Assert.False(session.Succeeded);
        Assert.Contains("budget", session.HaltReason);
        Assert.Equal(5, session.MetricsSnapshot().Attempts);
    }
}

public class GovernanceAndAuditTests
{
    [Fact]
    public void AuditLog_DetectsTampering()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.jsonl");
        using (var log = new AuditLog(path))
            for (var i = 0; i < 3; i++)
                log.OnEvent(new RunEvent("t", "run", "s", i, "actor", $"event {i}", new Dictionary<string, object?>(), DateTimeOffset.UtcNow));

        Assert.True(AuditLog.Verify(path).Valid);

        var lines = File.ReadAllLines(path);
        lines[1] = lines[1].Replace("event 1", "event X");
        File.WriteAllLines(path, lines);
        Assert.False(AuditLog.Verify(path).Valid);

        File.WriteAllLines(path, new[] { lines[0], lines[2] });
        Assert.False(AuditLog.Verify(path).Valid);
    }

    [Fact]
    public void Workspace_RefusesPathTraversal()
    {
        var ws = new Workspace.SandboxWorkspace(Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N")));

        Assert.Throws<UnauthorizedAccessException>(() => ws.Resolve("../outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => ws.Resolve("C:\\Windows\\win.ini"));
    }

    [Fact]
    public async Task RequirementsAgent_BlockingAmbiguity_AsksHumanInsteadOfGuessing()
    {
        var session = Session(Stage("requirements", "requirements"));
        session.Context.Put(Keys.Requirement, "Make it handle heavy traffic.", "human", 0, Array.Empty<ArtifactRef>());
        var human = new ScriptedHumanGateway(new Dictionary<string, string> { ["Q-TRAFFIC"] = "45 requests per minute" });

        await RunAsync(session, new IAgent[] { new RequirementsAgent(new DeterministicReasoningProvider()) }, human);

        var spec = session.Context.Require<RequirementSpec>(Keys.Requirements);
        Assert.True(session.Succeeded);
        Assert.Equal("45", spec.Parameters["rateLimitPerMinute"]);
        Assert.Contains(session.Bus.Events, e => e.Type == EventTypes.ClarificationRequested);
    }

    [Fact]
    public async Task RequirementsAgent_UnansweredBlockingAmbiguity_HaltsSafely()
    {
        var session = Session(Stage("requirements", "requirements"));
        session.Context.Put(Keys.Requirement, "Log everything about who clicks.", "human", 0, Array.Empty<ArtifactRef>());

        await RunAsync(session, new IAgent[] { new RequirementsAgent(new DeterministicReasoningProvider()) }, new DenyAllHumanGateway());

        Assert.False(session.Succeeded);
        Assert.Contains("Clarification unanswered", session.HaltReason);
    }
}
