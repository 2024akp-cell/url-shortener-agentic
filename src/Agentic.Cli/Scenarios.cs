using Agentic.Agents.Model;
using Agentic.Orchestration.Governance;
using Agentic.Orchestration.Workspace;

namespace Agentic.Cli;

public sealed record Scenario(
    string Name,
    string Description,
    string Requirement,
    bool SeedExistingCodebase,
    Action<SandboxWorkspace>? Setup,
    IReadOnlyDictionary<string, string> ScriptedAnswers,
    Func<ApprovalRequest, int, ApprovalDecision?>? ScriptedRule);

public static class Scenarios
{
    private static readonly Dictionary<string, string> AmbiguousAnswers = new()
    {
        ["Q-SAFETY"] = "Malicious/unsafe target URLs and API abuse.",
        ["Q-TRAFFIC"] = "60 requests per minute per client.",
        ["Q-LOGGING"] = "Referrer, user agent and a salted hash of the IP; raw IPs must not be stored or logged.",
    };

    public static IReadOnlyList<Scenario> All { get; } = new[]
    {
        new Scenario(
            "greenfield",
            "Build the URL shortener from an empty workspace (well-defined requirement with minor ambiguity).",
            "Build a URL shortener service. Users submit a long URL and get a short link; visiting the short link redirects to the original URL. "
            + "Support optional custom aliases and link expiration, and provide click analytics per link. The service must be reliable and secure.",
            SeedExistingCodebase: false,
            Setup: null,
            ScriptedAnswers: new Dictionary<string, string>(),
            ScriptedRule: null),

        new Scenario(
            "brownfield",
            "Fix a production regression in the existing codebase (impact analysis, baseline evidence, feedback loop, rollback).",
            "INC-1042: Expired short links still redirect to their target and are being counted in analytics. "
            + "Restore the documented behaviour: an expired link must return 410 Gone.",
            SeedExistingCodebase: true,
            Setup: InjectExpiryRegression,
            ScriptedAnswers: new Dictionary<string, string>(),
            ScriptedRule: null),

        new Scenario(
            "ambiguous",
            "Vague request with compliance risk (clarification, policy block → fallback, stakeholder change → re-plan).",
            "Make the shortener safer and make sure it can handle heavy traffic. Also log everything about who clicks our links.",
            SeedExistingCodebase: true,
            Setup: null,
            ScriptedAnswers: AmbiguousAnswers,
            // At the first design sign-off the security lead tightens the agreed rate limit → upstream artifact changes → re-plan.
            ScriptedRule: (request, nth) => request.StageId == "design" && nth == 0
                ? new ApprovalDecision(ApprovalOutcome.ApprovedWithChanges, "scripted:security-lead",
                    "Approve, but tighten the create limit to 30/min per client.",
                    new Dictionary<string, object>
                    {
                        [Keys.Clarifications] = new Dictionary<string, string>(AmbiguousAnswers) { ["Q-TRAFFIC"] = "30 requests per minute per client (tightened by security lead)." },
                    })
                : null),
    };

    public static Scenario? Find(string name) => All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Simulates the reported production defect by removing the expiry guard from the seeded code.</summary>
    private static void InjectExpiryRegression(SandboxWorkspace ws)
    {
        const string path = "src/UrlShortener.Core/Services/LinkService.cs";
        var text = ws.ReadText(path);
        var guard = text.Contains("\r\n", StringComparison.Ordinal)
            ? "        if (link.IsExpired(now))\r\n            return ServiceResult<ShortLink>.Fail(LinkError.Expired, \"Short link has expired.\");\r\n"
            : "        if (link.IsExpired(now))\n            return ServiceResult<ShortLink>.Fail(LinkError.Expired, \"Short link has expired.\");\n";
        if (!text.Contains(guard, StringComparison.Ordinal))
            throw new InvalidOperationException("Scenario setup failed: expiry guard not found in LinkService.cs.");
        File.WriteAllText(ws.Resolve(path), text.Replace(guard, ""));
    }

    /// <summary>Files that make up the existing service, copied into brownfield sandboxes.</summary>
    public static IEnumerable<string> ServiceFiles(string repoRoot) =>
        Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(repoRoot, f).Replace('\\', '/'))
            .Where(f => f == "Directory.Build.props" || f.StartsWith("src/UrlShortener.", StringComparison.Ordinal) || f.StartsWith("tests/UrlShortener.", StringComparison.Ordinal))
            .Where(f => !f.Split('/').Any(s => s is "bin" or "obj"))
            .Where(f => !f.EndsWith(".db", StringComparison.Ordinal) && !f.Contains(".db-", StringComparison.Ordinal));
}
