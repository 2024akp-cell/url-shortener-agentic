using System.Text;
using System.Text.RegularExpressions;
using Agentic.Agents.Knowledge;
using Agentic.Agents.Model;
using Agentic.Agents.Reasoning;
using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Context;

namespace Agentic.Agents.Stages;

/// <summary>
/// Interprets intent, detects ambiguity and normalises the request into a RequirementSpec.
/// Minor ambiguity → documented assumption. Blocking ambiguity → stops and asks a human.
/// </summary>
public sealed partial class RequirementsAgent : IAgent
{
    private readonly IReasoningProvider _reasoning;

    public RequirementsAgent(IReasoningProvider reasoning) => _reasoning = reasoning;

    public string Id => "requirements";

    public async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var requirement = ctx.Context.Require<string>(Keys.Requirement);
        var answers = ctx.Context.Get<Dictionary<string, string>>(Keys.Clarifications) ?? new Dictionary<string, string>();
        var interpretation = await _reasoning.InterpretAsync(requirement, ct);
        ctx.Log($"Reasoning provider '{_reasoning.Name}': {interpretation.Rationale}");

        var unknown = interpretation.CapabilityIds.Where(id => DomainKnowledge.Capabilities.All(c => c.Id != id)).ToList();
        if (unknown.Count > 0)
            return AgentResult.Failed($"Reasoning produced unknown capabilities: {string.Join(", ", unknown)}");
        if (interpretation.CapabilityIds.Count == 0)
            return AgentResult.Failed("Could not map the requirement to any known capability; needs human triage.");

        var unanswered = interpretation.Ambiguities
            .Where(a => a.Rule.Severity == AmbiguitySeverity.Blocking && !answers.ContainsKey(a.Rule.Id))
            .ToList();
        if (unanswered.Count > 0)
        {
            return new AgentResult
            {
                Summary = $"{unanswered.Count} blocking ambiguity(ies) need a human decision",
                Clarifications = unanswered
                    .Select(a => new ClarificationQuestion(a.Rule.Id, a.Rule.Question, a.Rule.WhyItMatters, a.Rule.DefaultAnswer))
                    .ToList(),
            };
        }

        var kind = ctx.Workspace.ListFiles(".cs").Any(f => f.StartsWith("src/", StringComparison.Ordinal))
            ? ChangeKind.Brownfield
            : ChangeKind.Greenfield;
        var capabilities = interpretation.CapabilityIds.Select(DomainKnowledge.Capability).ToList();
        var decisions = new List<DecisionDraft>
        {
            new(DecisionKind.Interpretation,
                $"{kind} change covering: {string.Join(", ", capabilities.Select(c => c.Name))}",
                interpretation.Rationale + (kind == ChangeKind.Brownfield ? " Existing source found in workspace → brownfield." : " Empty workspace → greenfield."),
                new[] { "Treat as single monolithic task", "Ask a human to decompose" }),
        };

        var ambiguities = new List<Ambiguity>();
        var assumptions = new List<string>();
        foreach (var hit in interpretation.Ambiguities)
        {
            if (answers.TryGetValue(hit.Rule.Id, out var answer))
            {
                ambiguities.Add(new Ambiguity(hit.Rule.Id, hit.MatchedPhrase, hit.Rule.Severity, hit.Rule.Question, answer, "human"));
            }
            else
            {
                ambiguities.Add(new Ambiguity(hit.Rule.Id, hit.MatchedPhrase, hit.Rule.Severity, hit.Rule.Question, hit.Rule.DefaultAnswer, "assumption"));
                assumptions.Add($"\"{hit.MatchedPhrase}\" interpreted as: {hit.Rule.DefaultAnswer}");
                decisions.Add(new DecisionDraft(DecisionKind.Assumption,
                    $"Assume {hit.Rule.Id}: {hit.Rule.DefaultAnswer}",
                    $"\"{hit.MatchedPhrase}\" is minor ambiguity; governance allows a documented assumption instead of blocking."));
            }
        }

        var parameters = DeriveParameters(answers);
        foreach (var (key, value) in parameters)
            decisions.Add(new DecisionDraft(DecisionKind.Interpretation, $"Parameter {key} = {value}", "Derived from clarification answers."));

        var criteria = capabilities
            .SelectMany(c => c.Acceptance.Select((a, i) => (c, a, i)))
            .Select((t, n) => new AcceptanceCriterion($"AC-{n + 1:00}", t.c.Id, t.a.Given, t.a.When, Specialise(t.a.Then, parameters)))
            .ToList();

        var spec = new RequirementSpec(
            Title: Title(requirement),
            Kind: kind,
            Intent: NormalisedIntent(kind, capabilities),
            Capabilities: capabilities.Select(c => c.Id).ToList(),
            FunctionalRequirements: capabilities.SelectMany(c => c.Functional).Select(f => Specialise(f, parameters)).ToList(),
            NonFunctionalRequirements: capabilities.SelectMany(c => c.NonFunctional).Distinct().ToList(),
            AcceptanceCriteria: criteria,
            Ambiguities: ambiguities,
            Assumptions: assumptions,
            OutOfScope: OutOfScope(kind),
            Parameters: parameters,
            Risks: capabilities.SelectMany(c => c.Risks).DistinctBy(r => r.Id).ToList());

        return new AgentResult
        {
            Summary = $"{spec.Kind}: {spec.Capabilities.Count} capabilities, {spec.AcceptanceCriteria.Count} acceptance criteria, {spec.Assumptions.Count} assumptions",
            Outputs = { [Keys.Requirements] = spec, [Keys.RequirementsDoc] = Render(spec, requirement) },
            Decisions = decisions,
        };
    }

    private static Dictionary<string, string> DeriveParameters(IReadOnlyDictionary<string, string> answers)
    {
        var parameters = new Dictionary<string, string>();
        if (answers.TryGetValue("Q-TRAFFIC", out var traffic) && FirstNumber().Match(traffic) is { Success: true } number)
            parameters["rateLimitPerMinute"] = number.Value;
        if (answers.TryGetValue("Q-LOGGING", out var logging))
            parameters["clientIdentity"] = logging.Contains("hash", StringComparison.OrdinalIgnoreCase) ? "hashed" : "unspecified";
        return parameters;
    }

    private static string Specialise(string text, IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue("rateLimitPerMinute", out var limit) && text.Contains("rate-limited", StringComparison.Ordinal)
            ? text.Replace("rate-limited per client address", $"rate-limited to {limit} requests/minute per client address")
            : text;

    private static string Title(string requirement)
    {
        var first = requirement.Split(new[] { '.', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return first.Length > 90 ? first[..87] + "..." : first;
    }

    private static string NormalisedIntent(ChangeKind kind, IReadOnlyList<CapabilityInfo> capabilities) => kind == ChangeKind.Greenfield
        ? $"Deliver a new URL shortener service providing: {string.Join("; ", capabilities.Select(c => c.Name.ToLowerInvariant()))}."
        : $"Change the existing URL shortener to: {string.Join("; ", capabilities.Select(c => c.Name.ToLowerInvariant()))}, without regressing existing behaviour.";

    private static IReadOnlyList<string> OutOfScope(ChangeKind kind) => kind == ChangeKind.Greenfield
        ? new[] { "User accounts / multi-tenant ownership of links", "Multi-region deployment and HA", "UI front-end" }
        : new[] { "Unrelated refactoring of modules outside the impact set", "Schema migrations (none required)" };

    private static string Render(RequirementSpec spec, string original)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 1. Requirements analysis");
        sb.AppendLine();
        sb.AppendLine($"**Normalised problem statement ({spec.Kind}).** {spec.Intent}");
        sb.AppendLine();
        sb.AppendLine("### Functional requirements");
        foreach (var f in spec.FunctionalRequirements)
            sb.AppendLine($"- {f}");
        sb.AppendLine();
        sb.AppendLine("### Non-functional requirements");
        foreach (var n in spec.NonFunctionalRequirements)
            sb.AppendLine($"- {n}");
        sb.AppendLine();
        sb.AppendLine("### Acceptance criteria");
        sb.AppendLine("| Id | Capability | Given | When | Then |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var ac in spec.AcceptanceCriteria)
            sb.AppendLine($"| {ac.Id} | {ac.Capability} | {ac.Given} | {ac.When} | {ac.Then} |");
        sb.AppendLine();
        sb.AppendLine("### Ambiguity resolution");
        if (spec.Ambiguities.Count == 0)
            sb.AppendLine("_No ambiguous phrasing detected._");
        else
        {
            sb.AppendLine("| Id | Phrase | Severity | Resolution | Resolved by |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var a in spec.Ambiguities)
                sb.AppendLine($"| {a.Id} | \"{a.Phrase}\" | {a.Severity} | {a.Resolution} | {a.ResolvedBy} |");
        }
        sb.AppendLine();
        if (spec.Parameters.Count > 0)
        {
            sb.AppendLine("### Derived parameters");
            foreach (var (k, v) in spec.Parameters)
                sb.AppendLine($"- `{k}` = **{v}**");
            sb.AppendLine();
        }
        sb.AppendLine("### Out of scope");
        foreach (var o in spec.OutOfScope)
            sb.AppendLine($"- {o}");
        sb.AppendLine();
        sb.AppendLine("### Risk register");
        sb.AppendLine("| Id | Risk | Likelihood | Impact | Mitigation |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in spec.Risks)
            sb.AppendLine($"| {r.Id} | {r.Description} | {r.Likelihood} | {r.Impact} | {r.Mitigation} |");
        return sb.ToString();
    }

    [GeneratedRegex("\\d+")]
    private static partial Regex FirstNumber();
}
