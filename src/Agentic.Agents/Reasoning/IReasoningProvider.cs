using Agentic.Agents.Knowledge;

namespace Agentic.Agents.Reasoning;

public sealed record AmbiguityHit(AmbiguityRule Rule, string MatchedPhrase);

public sealed record RequirementInterpretation(
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<AmbiguityHit> Ambiguities,
    string Rationale);

/// <summary>
/// The "thinking" step behind requirement understanding. Swappable: deterministic (default, offline,
/// reproducible) or LLM-backed. Its output is always validated against the known capability catalog,
/// so a model can never introduce an action the governance layer does not know about.
/// </summary>
public interface IReasoningProvider
{
    string Name { get; }

    Task<RequirementInterpretation> InterpretAsync(string requirement, CancellationToken ct);
}

public sealed class DeterministicReasoningProvider : IReasoningProvider
{
    public string Name => "deterministic-rules";

    public Task<RequirementInterpretation> InterpretAsync(string requirement, CancellationToken ct)
    {
        var text = " " + requirement.ToLowerInvariant() + " ";
        var capabilities = DomainKnowledge.Capabilities
            .Where(c => c.Keywords.Any(k => text.Contains(k, StringComparison.Ordinal)))
            .Select(c => c.Id)
            .ToList();

        // A bug report about expiry is a fix, not a request for the expiry feature.
        if (capabilities.Contains("fix-expired-redirect"))
            capabilities.Remove("expiry");

        var ambiguities = DomainKnowledge.Ambiguities
            .Select(rule => (rule, phrase: rule.Phrases.FirstOrDefault(p => text.Contains(p, StringComparison.Ordinal))))
            .Where(t => t.phrase is not null)
            .Select(t => new AmbiguityHit(t.rule, t.phrase!))
            .ToList();

        var rationale = $"Matched {capabilities.Count} capabilities and {ambiguities.Count} ambiguous phrases against the curated domain catalog.";
        return Task.FromResult(new RequirementInterpretation(capabilities, ambiguities, rationale));
    }
}
