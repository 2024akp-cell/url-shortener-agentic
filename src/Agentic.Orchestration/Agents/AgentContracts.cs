using Agentic.Orchestration.Context;
using Agentic.Orchestration.Workflow;
using Agentic.Orchestration.Workspace;

namespace Agentic.Orchestration.Agents;

/// <summary>
/// An agent performs one stage's work. It may read context and the workspace, but it cannot mutate
/// either directly: it returns outputs and a proposed <see cref="ChangeSet"/> that the engine
/// policy-checks, applies, validates and (if needed) sends for human approval.
/// </summary>
public interface IAgent
{
    string Id { get; }

    Task<AgentResult> ExecuteAsync(AgentContext context, CancellationToken ct);
}

public sealed record ClarificationQuestion(string Id, string Question, string WhyItMatters, string? SuggestedDefault);

/// <summary>Feedback from a previous failed attempt (or a downstream stage) so the agent can self-correct.</summary>
public sealed record AttemptFeedback(int Attempt, string Source, string Reason);

public sealed class AgentContext
{
    public required string RunId { get; init; }

    public required StageDefinition Stage { get; init; }

    public required int Attempt { get; init; }

    public required ContextStore Context { get; init; }

    public required SandboxWorkspace Workspace { get; init; }

    public required WorkflowGraph Graph { get; init; }

    public required TimeProvider Time { get; init; }

    public IReadOnlyList<AttemptFeedback> Feedback { get; init; } = Array.Empty<AttemptFeedback>();

    public required Action<string> Log { get; init; }

    public bool IsFallback { get; init; }
}

public sealed class AgentResult
{
    public bool Success { get; init; } = true;

    public string Summary { get; init; } = "";

    public string? Error { get; init; }

    public Dictionary<string, object> Outputs { get; init; } = new();

    public ChangeSet? Changes { get; init; }

    public List<DecisionDraft> Decisions { get; init; } = new();

    public GraphPatch? GraphPatch { get; init; }

    /// <summary>Non-empty when the agent cannot proceed without human input (ambiguity).</summary>
    public List<ClarificationQuestion> Clarifications { get; init; } = new();

    /// <summary>Upstream stages implicated in this failure; the engine re-opens them with feedback.</summary>
    public List<string> FeedbackTargets { get; init; } = new();

    public static AgentResult Failed(string error, string? summary = null) =>
        new() { Success = false, Error = error, Summary = summary ?? error };
}

public sealed class AgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.Ordinal);

    public AgentRegistry Register(IAgent agent)
    {
        _agents[agent.Id] = agent;
        return this;
    }

    public IAgent Resolve(string id) =>
        _agents.TryGetValue(id, out var agent) ? agent : throw new KeyNotFoundException($"No agent registered with id '{id}'.");

    public bool Contains(string id) => _agents.ContainsKey(id);
}
