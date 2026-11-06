using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Engine;
using Agentic.Orchestration.Governance;
using Agentic.Orchestration.Workflow;
using Agentic.Orchestration.Workspace;

namespace Agentic.Orchestration.Tests;

public sealed class DelegateAgent : IAgent
{
    private readonly Func<AgentContext, Task<AgentResult>> _run;

    public DelegateAgent(string id, Func<AgentContext, AgentResult> run)
        : this(id, ctx => Task.FromResult(run(ctx)))
    {
    }

    public DelegateAgent(string id, Func<AgentContext, Task<AgentResult>> run)
    {
        Id = id;
        _run = run;
    }

    public string Id { get; }

    public int Calls { get; private set; }

    public Task<AgentResult> ExecuteAsync(AgentContext context, CancellationToken ct)
    {
        Calls++;
        return _run(context);
    }
}

public static class Harness
{
    public static StageDefinition Stage(string id, string agent, params string[] dependsOn) => new()
    {
        Id = id,
        Name = id,
        Phase = SdlcPhase.Implementation,
        AgentId = agent,
        DependsOn = dependsOn,
        Retry = new RetryPolicy(1, TimeSpan.Zero),
    };

    public static RunSession Session(params StageDefinition[] stages)
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N"));
        return new RunSession("test-run", "test", dir, WorkflowGraph.Create("test", stages), new SandboxWorkspace(Path.Combine(dir, "ws")), TimeProvider.System);
    }

    public static async Task<RunSession> RunAsync(
        RunSession session,
        IEnumerable<IAgent> agents,
        IHumanGateway? human = null,
        GovernanceOptions? governance = null)
    {
        var registry = new AgentRegistry();
        foreach (var agent in agents)
            registry.Register(agent);
        governance ??= new GovernanceOptions();
        var orchestrator = new Orchestrator(registry, PolicyEngine.Default(governance), human ?? new ScriptedHumanGateway(), governance);
        return await orchestrator.RunAsync(session);
    }

    public static AgentResult Ok(string summary = "ok") => new() { Summary = summary };

    public static AgentResult Write(string path, string content) => new()
    {
        Summary = $"write {path}",
        Changes = new ChangeSet($"write {path}", new[] { FileChange.Write(path, content) }),
    };

    public static StageStatus StatusOf(RunSession session, string id) => session.Stages.Single(s => s.StageId == id).Status;
}
