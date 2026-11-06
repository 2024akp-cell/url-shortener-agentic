using Agentic.Agents.Knowledge;
using Agentic.Agents.Model;
using Agentic.Agents.Reasoning;
using Agentic.Agents.Stages;
using Agentic.Agents.Tooling;
using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Workflow;

namespace Agentic.Agents;

/// <summary>Declarative SDLC dependency graph and the agent roster that executes it.</summary>
public static class SdlcWorkflow
{
    public static AgentRegistry CreateAgents(string blueprintRoot, ITestRunner? testRunner = null, IReasoningProvider? reasoning = null)
    {
        var catalog = new ChangeCatalog(blueprintRoot);
        return new AgentRegistry()
            .Register(new RequirementsAgent(reasoning ?? new DeterministicReasoningProvider()))
            .Register(new CodebaseAnalysisAgent())
            .Register(new DesignAgent())
            .Register(new PlanningAgent(catalog))
            .Register(new ImplementationAgent(catalog))
            .Register(new ImplementationAgent(catalog, conservative: true))
            .Register(new TestAgent(testRunner ?? new DotnetTestRunner()))
            .Register(new SecurityReviewAgent())
            .Register(new DocumentationAgent())
            .Register(new ReleaseReadinessAgent());
    }

    /// <param name="existingCodebase">Adds the brownfield branch (codebase analysis → baseline tests) in parallel with requirements.</param>
    public static WorkflowGraph Create(string name, bool existingCodebase)
    {
        var stages = new List<StageDefinition>
        {
            new()
            {
                Id = "requirements", Name = "Requirements analysis", Phase = SdlcPhase.Requirements, AgentId = "requirements",
                Consumes = new[] { Keys.Requirement, Keys.Clarifications },
                Produces = new[] { Keys.Requirements, Keys.RequirementsDoc },
                EntryGates = new[] { Gates.ArtifactsPresent(Keys.Requirement) },
                ExitGates = new[] { Gates.ProducesDeclaredOutputs(), NoUnresolvedBlockingAmbiguity() },
                Retry = RetryPolicy.None,
            },
        };

        var designDependsOn = new List<string> { "requirements" };
        if (existingCodebase)
        {
            stages.Add(new()
            {
                Id = "codebase-analysis", Name = "Codebase analysis", Phase = SdlcPhase.Analysis, AgentId = "codebase-analysis",
                Produces = new[] { Keys.CodebaseModel, Keys.CodebaseDoc },
                ExitGates = new[] { Gates.ProducesDeclaredOutputs() },
            });
            stages.Add(new()
            {
                Id = "baseline-tests", Name = "Baseline test run", Phase = SdlcPhase.Testing, AgentId = "test",
                DependsOn = new[] { "codebase-analysis" },
                Produces = new[] { Keys.BaselineTests },
                Parameters = new Dictionary<string, string> { ["mode"] = "baseline" },
                Retry = new RetryPolicy(2, TimeSpan.FromSeconds(1)),
            });
            designDependsOn.Add("baseline-tests");
        }

        stages.AddRange(new StageDefinition[]
        {
            new()
            {
                Id = "design", Name = "Architecture & design", Phase = SdlcPhase.Design, AgentId = "design",
                DependsOn = designDependsOn,
                Consumes = new[] { Keys.Requirements, Keys.CodebaseModel },
                Produces = new[] { Keys.Design, Keys.DesignDoc },
                EntryGates = new[] { Gates.ArtifactsPresent(Keys.Requirements) },
                ExitGates = new[] { Gates.ProducesDeclaredOutputs() },
                RequiresApproval = true,
                Impact = ImpactLevel.Medium,
            },
            new()
            {
                Id = "planning", Name = "Task decomposition & planning", Phase = SdlcPhase.Planning, AgentId = "planning",
                DependsOn = new[] { "design" },
                Consumes = new[] { Keys.Requirements, Keys.Design, Keys.BaselineTests },
                Produces = new[] { Keys.Plan, Keys.PlanDoc },
                EntryGates = new[] { Gates.ArtifactsPresent(Keys.Requirements, Keys.Design) },
                ExitGates = new[] { Gates.ProducesDeclaredOutputs() },
            },
            new()
            {
                Id = "build-test", Name = "Build & test", Phase = SdlcPhase.Testing, AgentId = "test",
                DependsOn = new[] { "planning" },
                Consumes = new[] { Keys.Plan },
                Produces = new[] { Keys.TestReport, Keys.TestReportDoc },
                Retry = new RetryPolicy(2, TimeSpan.FromSeconds(1)),
                Timeout = TimeSpan.FromMinutes(8),
            },
            new()
            {
                Id = "security-review", Name = "Security & compliance review", Phase = SdlcPhase.Security, AgentId = "security-review",
                DependsOn = new[] { "build-test" },
                Produces = new[] { Keys.SecurityReview, Keys.SecurityReviewDoc },
                Retry = RetryPolicy.None,
            },
            new()
            {
                Id = "documentation", Name = "Documentation", Phase = SdlcPhase.Documentation, AgentId = "documentation",
                DependsOn = new[] { "build-test" },
                Consumes = new[] { Keys.Plan },
                Produces = new[] { Keys.Docs },
            },
            new()
            {
                Id = "release", Name = "Release readiness", Phase = SdlcPhase.Release, AgentId = "release-readiness",
                DependsOn = new[] { "security-review", "documentation" },
                Consumes = new[] { Keys.TestReport, Keys.SecurityReview, Keys.Docs },
                Produces = new[] { Keys.ReleaseReadiness, Keys.ReleaseReadinessDoc },
                EntryGates = new[] { Gates.ArtifactsPresent(Keys.TestReport, Keys.SecurityReview, Keys.Docs) },
                RequiresApproval = true,
                Impact = ImpactLevel.High,
                Retry = RetryPolicy.None,
            },
        });

        return WorkflowGraph.Create(name, stages);
    }

    private static IGate NoUnresolvedBlockingAmbiguity() => Gates.Custom("no-unresolved-blocking-ambiguity", ctx =>
    {
        var spec = ctx.Output<RequirementSpec>(Keys.Requirements);
        var open = spec?.Ambiguities.Where(a => a.Severity == AmbiguitySeverity.Blocking && a.ResolvedBy != "human").ToList();
        return open is { Count: > 0 }
            ? GateResult.Fail("blocking ambiguity resolved without a human: " + string.Join(", ", open.Select(a => a.Id)))
            : GateResult.Pass();
    });
}
