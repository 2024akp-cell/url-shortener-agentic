using Agentic.Agents.Model;

namespace Agentic.Agents.Knowledge;

public sealed record CapabilityInfo(
    string Id,
    string Name,
    string[] Keywords,
    string[] Functional,
    string[] NonFunctional,
    (string Given, string When, string Then)[] Acceptance,
    Risk[] Risks);

public sealed record AmbiguityRule(
    string Id,
    string[] Phrases,
    AmbiguitySeverity Severity,
    string Question,
    string WhyItMatters,
    string DefaultAnswer);

/// <summary>
/// Curated domain knowledge used by the deterministic reasoning provider. In LLM mode an
/// <see cref="Reasoning.IReasoningProvider"/> replaces keyword matching with model inference, but the
/// downstream contracts (RequirementSpec, TaskPlan) and all governance stay identical.
/// </summary>
public static class DomainKnowledge
{
    public static readonly IReadOnlyList<CapabilityInfo> Capabilities = new[]
    {
        new CapabilityInfo("shorten", "Create short links", new[] { "shorten", "short code", "short link", "url shortener" },
            new[] { "POST /api/links accepts a long URL and returns a unique short code and short URL (201).", "Codes are 7-char random Base62; collisions retried a bounded number of times." },
            new[] { "Target URLs are validated: http/https only, <= 2048 chars, no credentials, no loopback/private hosts (SSRF / open-redirect abuse)." },
            new[] { ("a valid public https URL", "it is submitted to POST /api/links", "a 201 with a 7-char code and Location header is returned"),
                    ("a javascript: or private-network URL", "it is submitted", "a 400 validation problem is returned") },
            new[] { new Risk("R-ENUM", "Sequential codes allow link enumeration", "Medium", "Medium", "Cryptographically random codes (62^7 keyspace)") }),
        new CapabilityInfo("redirect", "Redirect", new[] { "redirect", "visit" },
            new[] { "GET /{code} responds 302 to the target; 404 for unknown codes." },
            new[] { "Redirect responses are not cacheable (Cache-Control: no-store) so every visit is counted." },
            new[] { ("an existing code", "GET /{code} is requested", "a 302 to the target URL is returned") },
            new[] { new Risk("R-HOT", "Redirect path is the hot path; synchronous click write adds latency", "Medium", "Medium", "Indexed insert; async queue noted as scale-out follow-up") }),
        new CapabilityInfo("custom-alias", "Custom aliases", new[] { "alias", "custom code", "vanity" },
            new[] { "Optional customAlias (3-32 chars [A-Za-z0-9_-]); reserved words rejected; duplicates return 409." },
            Array.Empty<string>(),
            new[] { ("an alias already in use", "it is requested again", "a 409 conflict is returned") },
            Array.Empty<Risk>()),
        new CapabilityInfo("expiry", "Link expiration", new[] { "expire", "expiry", "expiration" },
            new[] { "Optional expiresAt (future, <= 365 days). Expired links return 410 Gone and record no click." },
            Array.Empty<string>(),
            new[] { ("a link past its expiresAt", "GET /{code} is requested", "a 410 Gone is returned and no click is recorded") },
            new[] { new Risk("R-CLOCK", "Clock skew between nodes affects expiry", "Low", "Low", "UTC TimeProvider abstraction; single clock source") }),
        new CapabilityInfo("analytics", "Click analytics", new[] { "analytics", "click", "stats", "statistics" },
            new[] { "GET /api/links/{code}/stats returns total clicks, unique visitors, daily series and top referrers." },
            new[] { "Client addresses are stored only as salted hashes (privacy by design)." },
            new[] { ("a link clicked 3 times with a referrer", "stats are requested", "totalClicks = 3 and the referrer is listed") },
            new[] { new Risk("R-PII", "Analytics could retain personal data", "Medium", "High", "Salted SHA-256 hash of client address; no raw IP persisted or logged") }),
        new CapabilityInfo("fix-expired-redirect", "Fix: expired links still redirect", new[] { "still redirect", "regression", "bug", "incident" },
            new[] { "Expired links must return 410 Gone and must not be counted as clicks (restores documented behaviour)." },
            Array.Empty<string>(),
            new[] { ("an expired link", "it is resolved by the domain service", "LinkError.Expired is returned and no click is recorded"),
                    ("an expired link", "GET /{code} is requested", "a 410 Gone is returned") },
            new[] { new Risk("R-REG", "Fix applied at the HTTP edge only leaves other callers of the domain service broken", "Medium", "High", "Fix the invariant in LinkService; cover with unit and integration tests") }),
        new CapabilityInfo("batch-create", "Batch link creation", new[] { "batch", "bulk" },
            new[] { "POST /api/links/batch creates up to 50 links in one call, returning a per-item result (partial success allowed)." },
            new[] { "Batch requests are bounded (<= 50 items) to cap work per request." },
            new[] { ("a batch with one valid and one invalid URL", "it is submitted", "a 200 with one created item and one item-level error is returned"),
                    ("a batch of 51 items", "it is submitted", "a 400 is returned") },
            new[] { new Risk("R-AMP", "Batch endpoint amplifies abuse", "Medium", "Medium", "Item cap + same rate-limit policy as single create") }),
        new CapabilityInfo("abuse-protection", "Rate limiting", new[] { "traffic", "rate limit", "throttl", "abuse", "load" },
            new[] { "Link creation is rate-limited per client address; excess requests get 429 with Retry-After." },
            new[] { "Limits are configuration-driven (RateLimiting:PermitLimit / WindowSeconds)." },
            new[] { ("a client that exceeded the per-minute limit", "it creates another link", "a 429 with Retry-After is returned") },
            new[] { new Risk("R-NAT", "Per-IP limits penalise users behind shared NAT", "Medium", "Low", "Configurable limit; API-key partitioning as follow-up") }),
        new CapabilityInfo("url-safety", "Unsafe target protection", new[] { "safer", "safe", "malicious", "phishing" },
            new[] { "Unsafe targets (non-http schemes, embedded credentials, loopback/private/link-local hosts, own domain) are rejected." },
            Array.Empty<string>(),
            new[] { ("a URL pointing at 169.254.169.254", "it is submitted", "a 400 validation problem is returned") },
            new[] { new Risk("R-DNS", "Public hostnames that resolve to private IPs bypass literal-IP checks", "Low", "Medium", "Documented limitation; resolve-and-check at redirect time as follow-up") }),
        new CapabilityInfo("click-logging", "Click audit logging", new[] { "log everything", "who clicks", "log " },
            new[] { "Each redirect emits a structured log event (code, referrer host, user-agent, hashed client id)." },
            new[] { "Logs must not contain raw client IPs or other direct identifiers (compliance)." },
            new[] { ("a redirect", "it is served", "a structured log line without the raw client IP is written") },
            new[] { new Risk("R-LOGPII", "Raw IPs in logs breach data-minimisation obligations", "High", "High", "PII logging guardrail (CMP-001) + hashed identifiers") }),
    };

    public static readonly IReadOnlyList<AmbiguityRule> Ambiguities = new[]
    {
        new AmbiguityRule("Q-RELIABILITY", new[] { "reliable", "reliability" }, AmbiguitySeverity.Minor,
            "What availability and durability targets apply?",
            "Determines storage, redundancy and failure handling.",
            "Single-node prototype: durable SQLite (WAL), /health endpoint, bounded retries; multi-node HA out of scope."),
        new AmbiguityRule("Q-SECURITY", new[] { "secure", "security" }, AmbiguitySeverity.Minor,
            "Which security controls are required?",
            "Determines validation, authn and abuse controls.",
            "OWASP baseline: input validation, SSRF/open-redirect guard, per-client rate limiting, optional API key on management endpoints, no raw PII stored."),
        new AmbiguityRule("Q-PERF", new[] { "fast", "quickly", "low latency" }, AmbiguitySeverity.Minor,
            "What latency target applies?",
            "Determines caching and storage choices.",
            "p95 redirect < 50 ms on a single node (not load-tested in this prototype)."),
        new AmbiguityRule("Q-SAFETY", new[] { "safer", "more safe" }, AmbiguitySeverity.Blocking,
            "\"Safer\" against which threats — malicious targets, abuse of the API, or data exposure?",
            "Each threat implies different controls; guessing risks building the wrong thing.",
            "Malicious/unsafe target URLs and API abuse."),
        new AmbiguityRule("Q-TRAFFIC", new[] { "heavy traffic", "lots of traffic", "high traffic", "a lot of traffic" }, AmbiguitySeverity.Blocking,
            "\"Heavy traffic\": what per-client creation rate should be allowed before throttling (requests/minute)?",
            "The limit is a product decision with customer impact; it must not be invented by an agent.",
            "60 requests per minute per client."),
        new AmbiguityRule("Q-LOGGING", new[] { "log everything", "everything about who" }, AmbiguitySeverity.Blocking,
            "\"Log everything about who clicks\": which visitor attributes may be recorded, and may raw IP addresses be kept?",
            "Recording personal data has compliance implications (data minimisation).",
            "Referrer, user agent and a salted hash of the IP; raw IPs must not be stored or logged."),
    };

    public static CapabilityInfo Capability(string id) => Capabilities.Single(c => c.Id == id);
}
