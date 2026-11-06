using System.Text;
using Agentic.Agents.Model;
using Agentic.Agents.Tooling;
using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Context;

namespace Agentic.Agents.Stages;

/// <summary>
/// Builds and tests the sandbox for real. In "baseline" mode it only records evidence (brownfield
/// bug confirmation). In "gate" mode failures fail the stage and are attributed to the
/// implementation stages whose tasks claimed those tests, so the engine can re-open exactly them.
/// </summary>
public sealed class TestAgent : IAgent
{
    private readonly ITestRunner _runner;

    public TestAgent(ITestRunner runner) => _runner = runner;

    public string Id => "test";

    public async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var baseline = ctx.Stage.Parameter("mode") == "baseline";
        ctx.Log("Running dotnet build + test in sandbox...");
        var report = await _runner.RunAsync(ctx.Workspace.Root, ct);
        ctx.Log($"Build {(report.BuildSucceeded ? "ok" : "FAILED")}; {report.Passed}/{report.Total} passed in {report.DurationSeconds}s");

        var outputKey = baseline ? Keys.BaselineTests : Keys.TestReport;
        var doc = Render(report, baseline);
        var outputs = new Dictionary<string, object> { [outputKey] = report };
        if (!baseline)
            outputs[Keys.TestReportDoc] = doc;

        if (baseline)
        {
            var summary = report.Failed > 0
                ? $"Baseline reproduces defect: {report.Failed} failing test(s): {string.Join(", ", report.FailedTests.Select(Short))}"
                : $"Baseline green: {report.Passed}/{report.Total}";
            return new AgentResult
            {
                Summary = summary,
                Outputs = outputs,
                Decisions = { new DecisionDraft(DecisionKind.Interpretation, summary, "Baseline run establishes evidence before any change is made.") },
            };
        }

        if (report.BuildSucceeded && report.Failed == 0)
            return new AgentResult { Summary = $"All {report.Total} tests passed", Outputs = outputs };

        var targets = Attribute(ctx, report);
        var reason = report.BuildSucceeded
            ? $"{report.Failed} test(s) failed: {string.Join(", ", report.FailedTests.Select(Short))}"
            : $"Build failed: {string.Join(" | ", report.BuildErrors.Take(3))}";
        return new AgentResult
        {
            Success = false,
            Error = reason,
            Summary = reason,
            Outputs = outputs,
            FeedbackTargets = targets,
        };
    }

    /// <summary>Maps failures to the implementation stages responsible (via plan.VerifiedBy or touched files).</summary>
    private static List<string> Attribute(AgentContext ctx, TestReport report)
    {
        var plan = ctx.Context.Get<TaskPlan>(Keys.Plan);
        if (plan is null)
            return new List<string>();

        var targets = new HashSet<string>();
        foreach (var task in plan.Tasks)
        {
            var stageId = TaskPlan.StageIdFor(task.Id);
            if (!ctx.Graph.Contains(stageId))
                continue;
            if (report.FailedTests.Any(t => task.VerifiedBy.Any(v => t.Contains(v, StringComparison.Ordinal))))
                targets.Add(stageId);
            if (report.BuildErrors.Any(e => task.Files.Any(f => e.Contains(Path.GetFileName(f), StringComparison.Ordinal))))
                targets.Add(stageId);
        }
        return targets.ToList();
    }

    private static string Short(string testName) => testName.Split('.').TakeLast(2).Aggregate((a, b) => $"{a}.{b}");

    private static string Render(TestReport report, bool baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine(baseline ? "## Baseline test run" : "## 5. Verification");
        sb.AppendLine();
        sb.AppendLine($"- Command: `{report.Command}`");
        sb.AppendLine($"- Build: {(report.BuildSucceeded ? "succeeded" : "failed")}");
        sb.AppendLine($"- Tests: **{report.Passed}/{report.Total} passed**, {report.Failed} failed ({report.DurationSeconds}s)");
        foreach (var failed in report.FailedTests)
            sb.AppendLine($"  - ❌ `{failed}`");
        foreach (var error in report.BuildErrors)
            sb.AppendLine($"  - 🛑 `{error}`");
        return sb.ToString();
    }
}
