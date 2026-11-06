using Agentic.Agents.Model;
using Agentic.Agents.Tooling;
using Agentic.Orchestration.Workspace;

namespace Agentic.Agents.Knowledge;

/// <summary>One way of implementing a task. Conservative candidates are the minimal-risk fallback.</summary>
public sealed record ChangeCandidate(
    string Strategy,
    string Rationale,
    bool Conservative,
    Func<SandboxWorkspace, RequirementSpec, ChangeSet> Build);

/// <summary>
/// Reviewed "golden path" changes the deterministic implementation agent can propose, ordered from
/// first choice to last. Greenfield code comes from the reference blueprint (this repository's
/// service). An LLM implementation agent would generate these instead; governance is unchanged.
/// </summary>
public sealed class ChangeCatalog
{
    private readonly string _blueprintRoot;

    public ChangeCatalog(string blueprintRoot) => _blueprintRoot = blueprintRoot;

    public IReadOnlyList<ChangeCandidate> For(string taskId) => taskId switch
    {
        "scaffold" => new[] { Blueprint("Directory.Build.props + project files", "Directory.Build.props", "*.csproj") },
        "domain" => new[] { Blueprint("Domain model and LinkService", "src/UrlShortener.Core/") },
        "persistence" => new[] { Blueprint("SQLite + in-memory repositories", "src/UrlShortener.Infrastructure/") },
        "api" => new[] { Blueprint("Minimal API endpoints, rate limiting, problem details", "src/UrlShortener.Api/") },
        "tests" => new[] { Blueprint("Unit + integration test suite", "tests/UrlShortener.Tests/") },
        "fix-expiry" => new[] { FixAtHttpEdge, FixDomainInvariant },
        "rate-limit" => new[] { TuneRateLimit },
        "click-logging" => new[] { LogFullVisitorDetails, LogMinimisedVisitorDetails },
        _ => Array.Empty<ChangeCandidate>(),
    };

    public bool HasConservative(string taskId) => For(taskId).Any(c => c.Conservative);

    private ChangeCandidate Blueprint(string description, params string[] scopes) => new(
        "blueprint:" + description,
        "Golden-path module from the reviewed reference implementation.",
        Conservative: false,
        (_, _) => new ChangeSet(description, BlueprintFiles(scopes).Select(f => FileChange.Write(f, File.ReadAllText(Path.Combine(_blueprintRoot, f)))).ToList()));

    private IEnumerable<string> BlueprintFiles(string[] scopes)
    {
        var all = Directory.EnumerateFiles(_blueprintRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_blueprintRoot, f).Replace('\\', '/'))
            .Where(f => !f.Split('/').Any(s => s is "bin" or "obj" or "Properties"))
            .Where(f => f.StartsWith("src/UrlShortener.", StringComparison.Ordinal) || f.StartsWith("tests/UrlShortener.", StringComparison.Ordinal) || f == "Directory.Build.props")
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal));
        return all.Where(f => scopes.Any(scope => scope.StartsWith("*.", StringComparison.Ordinal)
                ? f.EndsWith(scope[1..], StringComparison.Ordinal)
                : scope.EndsWith('/') ? f.StartsWith(scope, StringComparison.Ordinal) && !f.EndsWith(".csproj", StringComparison.Ordinal) : f == scope))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    // --- Brownfield: INC-1042 expired links still redirect -------------------------------------

    private static readonly ChangeCandidate FixAtHttpEdge = new(
        "Guard expiry in the redirect endpoint",
        "Smallest diff: reject expired links at the HTTP layer before redirecting.",
        Conservative: false,
        (ws, _) => new ChangeSet("Check expiry in RedirectEndpoints before redirecting", new[]
        {
            TextPatch.Replace(ws, "src/UrlShortener.Api/Endpoints/RedirectEndpoints.cs",
                ("ILoggerFactory loggers, CancellationToken ct)", "ILoggerFactory loggers, TimeProvider time, CancellationToken ct)"),
                ("        if (!result.IsSuccess)\n            return result.ToProblem();\n",
                 "        if (!result.IsSuccess)\n            return result.ToProblem();\n        if (result.Value!.IsExpired(time.GetUtcNow()))\n            return Results.Problem(statusCode: StatusCodes.Status410Gone, title: \"Short link has expired.\");\n")),
        }));

    private static readonly ChangeCandidate FixDomainInvariant = new(
        "Restore the expiry invariant in LinkService.ResolveAsync",
        "Root cause is in the domain service; fixing it there protects every caller and stops expired clicks being recorded.",
        Conservative: true,
        (ws, _) => new ChangeSet("Reject expired links in LinkService.ResolveAsync", new[]
        {
            TextPatch.Replace(ws, "src/UrlShortener.Core/Services/LinkService.cs",
                ("        var now = _time.GetUtcNow();\n\n        await _clicks.AddAsync(",
                 "        var now = _time.GetUtcNow();\n        if (link.IsExpired(now))\n            return ServiceResult<ShortLink>.Fail(LinkError.Expired, \"Short link has expired.\");\n\n        await _clicks.AddAsync(")),
        }));

    // --- Ambiguous: "safer / heavy traffic / log everything" -----------------------------------

    private static readonly ChangeCandidate TuneRateLimit = new(
        "Tune the existing fixed-window limiter via configuration",
        "Rate limiting already exists; the agreed limit is a configuration change plus a test that pins it.",
        Conservative: true,
        (ws, spec) =>
        {
            var limit = spec.Parameters.TryGetValue("rateLimitPerMinute", out var value) ? value : "60";
            return new ChangeSet($"Set create rate limit to {limit}/minute", new[]
            {
                TextPatch.Replace(ws, "src/UrlShortener.Api/appsettings.json", ("\"PermitLimit\": 100", $"\"PermitLimit\": {limit}")),
                FileChange.Write("tests/UrlShortener.Tests/Integration/RateLimitPolicyTests.cs", RateLimitTest(limit)),
            });
        });

    private static readonly ChangeCandidate LogFullVisitorDetails = new(
        "Log every visitor attribute including client IP",
        "Literal reading of \"log everything about who clicks\".",
        Conservative: false,
        (ws, _) => new ChangeSet("Structured redirect log with full visitor details", new[]
        {
            TextPatch.Replace(ws, "src/UrlShortener.Api/Endpoints/RedirectEndpoints.cs",
                ("        // 302 + no-store",
                 "        logger.LogInformation(\"Redirect {Code} from {ClientIp} referrer {Referrer} agent {UserAgent}\", code, http.Connection.RemoteIpAddress, click.Referrer, click.UserAgent);\n\n        // 302 + no-store")),
        }));

    private static readonly ChangeCandidate LogMinimisedVisitorDetails = new(
        "Log minimised visitor details (no direct identifiers)",
        "Meets the clarified need (who/where from) while honouring data minimisation: referrer host and user agent only; the hashed id stays in analytics storage.",
        Conservative: true,
        (ws, _) => new ChangeSet("Structured redirect log without personal data", new[]
        {
            TextPatch.Replace(ws, "src/UrlShortener.Api/Endpoints/RedirectEndpoints.cs",
                ("        // 302 + no-store",
                 "        logger.LogInformation(\"Redirect {Code} referrer {ReferrerHost} agent {UserAgent}\", code, ReferrerHost(click.Referrer), click.UserAgent);\n\n        // 302 + no-store"),
                ("    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;",
                 "    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;\n\n    private static string? ReferrerHost(string? referrer) =>\n        Uri.TryCreate(referrer, UriKind.Absolute, out var uri) ? uri.Host : null;")),
        }));

    private static string RateLimitTest(string limit) => $$"""
        using System.Text.Json;

        namespace UrlShortener.Tests.Integration;

        public class RateLimitPolicyTests
        {
            [Fact]
            public void ConfiguredCreateRateLimit_MatchesAgreedPolicy()
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "UrlShortener.Api", "appsettings.json")))
                    dir = dir.Parent;
                Assert.NotNull(dir);

                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "UrlShortener.Api", "appsettings.json")));
                var limit = json.RootElement.GetProperty("RateLimiting").GetProperty("PermitLimit").GetInt32();

                Assert.Equal({{limit}}, limit);
            }
        }

        """;
}
