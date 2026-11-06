using System.Text;
using Agentic.Agents.Knowledge;
using Agentic.Agents.Model;
using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Context;
using Agentic.Orchestration.Workflow;

namespace Agentic.Agents.Stages;

/// <summary>Produces the architecture/design decision record and API contract.</summary>
public sealed class DesignAgent : IAgent
{
    public string Id => "design";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var spec = ctx.Context.Require<RequirementSpec>(Keys.Requirements);
        var codebase = ctx.Context.Get<CodebaseModel>(Keys.CodebaseModel);
        var caps = spec.Capabilities.ToHashSet();

        var api = new List<ApiOperation>();
        if (spec.Kind == ChangeKind.Greenfield)
        {
            api.Add(new("POST", "/api/links", "Create short link (optional customAlias, expiresAt)", "201, 400, 409, 429"));
            api.Add(new("GET", "/{code}", "Redirect to target", "302, 404, 410"));
            api.Add(new("GET", "/api/links/{code}", "Link details", "200, 404"));
            api.Add(new("DELETE", "/api/links/{code}", "Delete link and its analytics", "204, 404"));
            api.Add(new("GET", "/api/links/{code}/stats?days=30", "Click analytics", "200, 404"));
            api.Add(new("GET", "/health", "Liveness", "200"));
        }

        var decisions = new List<string>();
        var impacted = new List<string>();
        if (spec.Kind == ChangeKind.Greenfield)
        {
            decisions.AddRange(new[]
            {
                "ADR-1 Layered ports-and-adapters: Api → Core ← Infrastructure. Business rules live only in Core.",
                "ADR-2 SQLite (WAL) behind ILinkRepository/IClickRepository; swappable for Postgres/Redis without touching Core.",
                "ADR-3 7-char cryptographically random Base62 codes; bounded collision retries.",
                "ADR-4 302 + Cache-Control: no-store so analytics see every visit (301 would be cached).",
                "ADR-5 Expected outcomes are ServiceResult values mapped to RFC 9457 problem details; exceptions only for faults.",
                "ADR-6 Privacy by design: client address stored only as a salted hash.",
            });
        }
        if (caps.Contains("fix-expired-redirect"))
        {
            decisions.Add("Fix the expiry invariant in the domain service (LinkService.ResolveAsync), not at the HTTP edge, so all callers are protected and no click is recorded.");
            impacted.AddRange(Find(codebase, "LinkService", "RedirectEndpoints"));
        }
        if (caps.Contains("abuse-protection"))
        {
            decisions.Add($"Reuse the existing fixed-window limiter; set PermitLimit to the agreed {spec.Parameters.GetValueOrDefault("rateLimitPerMinute", "default")}/min via configuration (no new code path).");
            impacted.Add("src/UrlShortener.Api/appsettings.json");
        }
        if (caps.Contains("click-logging"))
        {
            decisions.Add("Emit one structured log event per redirect in RedirectEndpoints; identifiers must respect the clarified data-minimisation rule.");
            impacted.AddRange(Find(codebase, "RedirectEndpoints"));
        }
        if (caps.Contains("url-safety") && spec.Kind == ChangeKind.Brownfield)
            decisions.Add("Unsafe-target protection already exists in UrlValidator (scheme, credential, loopback/private/link-local checks); verify via existing tests rather than change code.");

        var design = new DesignSpec(
            Components: new[] { "UrlShortener.Api (HTTP, rate limiting, correlation id, problem details)", "UrlShortener.Core (LinkService, UrlValidator, Base62CodeGenerator)", "UrlShortener.Infrastructure (SQLite + in-memory repositories)" },
            Api: api,
            DataModel: new[] { "links(code PK, target_url, created_at, expires_at?, is_custom)", "clicks(id, code, occurred_at, referrer?, user_agent?, client_hash?) + index(code, occurred_at)" },
            Decisions: decisions,
            ImpactedFiles: impacted.Distinct().ToList());

        return Task.FromResult(new AgentResult
        {
            Summary = $"{decisions.Count} design decisions, {api.Count} API operations, {design.ImpactedFiles.Count} impacted files",
            Outputs = { [Keys.Design] = design, [Keys.DesignDoc] = Render(design) },
            Decisions = decisions.Select(d => new DecisionDraft(DecisionKind.Design, d, "See design record.")).ToList(),
        });
    }

    private static IEnumerable<string> Find(CodebaseModel? model, params string[] types) =>
        model?.Files.Where(f => f.Types.Any(types.Contains)).Select(f => f.Path) ?? Enumerable.Empty<string>();

    private static string Render(DesignSpec d)
    {
        var sb = new StringBuilder("## 3. Architecture & design\n\n");
        sb.AppendLine("### Components");
        foreach (var c in d.Components)
            sb.AppendLine($"- {c}");
        if (d.Api.Count > 0)
        {
            sb.AppendLine("\n### API contract");
            sb.AppendLine("| Method | Route | Purpose | Responses |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var op in d.Api)
                sb.AppendLine($"| {op.Method} | `{op.Route}` | {op.Purpose} | {op.Responses} |");
            sb.AppendLine("\n### Data model");
            foreach (var t in d.DataModel)
                sb.AppendLine($"- `{t}`");
        }
        sb.AppendLine("\n### Key decisions");
        foreach (var dec in d.Decisions)
            sb.AppendLine($"- {dec}");
        if (d.ImpactedFiles.Count > 0)
        {
            sb.AppendLine("\n### Impacted files");
            foreach (var f in d.ImpactedFiles)
                sb.AppendLine($"- `{f}`");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Decomposes the design into tasks with dependencies, then expands the workflow graph at runtime with
/// one implementation stage per task (parallel where the dependencies allow), synchronised on build/test.
/// </summary>
public sealed class PlanningAgent : IAgent
{
    private readonly ChangeCatalog _catalog;

    public PlanningAgent(ChangeCatalog catalog) => _catalog = catalog;

    public string Id => "planning";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var spec = ctx.Context.Require<RequirementSpec>(Keys.Requirements);
        var baseline = ctx.Context.Get<TestReport>(Keys.BaselineTests);
        var tasks = new List<PlannedTask>();
        var satisfied = new List<string>();

        if (spec.Kind == ChangeKind.Greenfield)
        {
            tasks.Add(new("scaffold", "Solution scaffold & build config", "all", Array.Empty<string>(), new[] { "Directory.Build.props", "*.csproj" }, "Low", Array.Empty<string>(), "Everything else compiles against it."));
            tasks.Add(new("domain", "Domain model & link service", "shorten, redirect, custom-alias, expiry", new[] { "scaffold" }, new[] { "src/UrlShortener.Core/" }, "Medium", new[] { "LinkServiceTests", "UrlValidatorTests", "Base62CodeGeneratorTests" }, "Core business rules; other layers depend on its ports."));
            tasks.Add(new("persistence", "SQLite + in-memory repositories", "analytics", new[] { "domain" }, new[] { "src/UrlShortener.Infrastructure/" }, "Medium", new[] { "SqliteRepositoryTests" }, "Implements Core ports; independent of API."));
            tasks.Add(new("api", "HTTP API, rate limiting, security", "redirect, analytics", new[] { "domain" }, new[] { "src/UrlShortener.Api/" }, "Medium", new[] { "LinkApiTests", "SecurityAndLimitsTests" }, "Depends only on Core contracts, so runs in parallel with persistence."));
            tasks.Add(new("tests", "Unit & integration tests", "all", new[] { "domain" }, new[] { "tests/UrlShortener.Tests/" }, "Low", Array.Empty<string>(), "Written against contracts; parallel with persistence and API."));
        }
        else
        {
            foreach (var cap in spec.Capabilities)
            {
                switch (cap)
                {
                    case "fix-expired-redirect":
                        var evidence = baseline is { Failed: > 0 } ? $"Baseline reproduces it: {string.Join(", ", baseline.FailedTests.Select(t => t.Split('.').Last()))}." : "Baseline did not reproduce the defect.";
                        tasks.Add(new("fix-expiry", "Fix INC-1042: expired links still redirect", cap, Array.Empty<string>(), new[] { "src/UrlShortener.Core/Services/LinkService.cs" }, "High",
                            new[] { "ExpiredLink" }, evidence));
                        break;
                    case "abuse-protection" when spec.Parameters.ContainsKey("rateLimitPerMinute"):
                        tasks.Add(new("rate-limit", $"Set create rate limit to {spec.Parameters["rateLimitPerMinute"]}/min", cap, Array.Empty<string>(), new[] { "src/UrlShortener.Api/appsettings.json" }, "Low",
                            new[] { "RateLimitPolicyTests", "OverRateLimit" }, "Limit agreed with stakeholder; configuration-only change."));
                        break;
                    case "click-logging":
                        tasks.Add(new("click-logging", "Structured redirect logging", cap, Array.Empty<string>(), new[] { "src/UrlShortener.Api/Endpoints/RedirectEndpoints.cs" }, "Medium",
                            new[] { "Redirect_" }, "Touches the hot path; must satisfy compliance guardrail."));
                        break;
                    default:
                        satisfied.Add($"{cap}: already implemented in the existing codebase — verified by existing tests, no change planned.");
                        break;
                }
            }
        }

        if (tasks.Count == 0)
            return Task.FromResult(AgentResult.Failed("Planning produced no actionable tasks."));

        var plan = new TaskPlan(tasks, satisfied);
        var stages = tasks.Select(t => new StageDefinition
        {
            Id = TaskPlan.StageIdFor(t.Id),
            Name = $"Implement: {t.Title}",
            Phase = SdlcPhase.Implementation,
            AgentId = "implementation",
            DependsOn = t.DependsOn.Select(TaskPlan.StageIdFor).ToList(),
            Consumes = new[] { Keys.Plan, Keys.Requirements },
            Impact = t.Risk == "High" ? ImpactLevel.High : ImpactLevel.Medium,
            // A strategy with a compliant fallback gets one shot, then the conservative agent takes over.
            Retry = _catalog.HasConservative(t.Id) && t.Id == "click-logging" ? RetryPolicy.None : new RetryPolicy(2, TimeSpan.FromMilliseconds(100)),
            FallbackAgentId = t.Id == "click-logging" ? "implementation.conservative" : null,
            Parameters = new Dictionary<string, string> { ["task"] = t.Id },
        }).ToList();

        var decisions = new List<DecisionDraft>
        {
            new(DecisionKind.Plan, $"{tasks.Count} tasks; parallel groups: {ParallelGroups(tasks)}",
                "Dependencies follow the project-reference graph so independent layers run concurrently.",
                new[] { "Single sequential implementation stage" }),
        };
        decisions.AddRange(satisfied.Select(s => new DecisionDraft(DecisionKind.Plan, s, "Avoids unnecessary change (minimal blast radius).")));

        return Task.FromResult(new AgentResult
        {
            Summary = $"{tasks.Count} tasks planned, {satisfied.Count} capabilities already satisfied",
            Outputs = { [Keys.Plan] = plan, [Keys.PlanDoc] = Render(plan) },
            Decisions = decisions,
            GraphPatch = new GraphPatch(stages, new[] { "build-test" }),
        });
    }

    private static string ParallelGroups(IReadOnlyList<PlannedTask> tasks) =>
        string.Join(" → ", tasks.GroupBy(t => Depth(t, tasks)).OrderBy(g => g.Key).Select(g => "[" + string.Join(" ∥ ", g.Select(t => t.Id)) + "]"));

    private static int Depth(PlannedTask task, IReadOnlyList<PlannedTask> all) =>
        task.DependsOn.Count == 0 ? 0 : task.DependsOn.Max(d => Depth(all.Single(t => t.Id == d), all)) + 1;

    private static string Render(TaskPlan plan)
    {
        var sb = new StringBuilder("## 4. Task decomposition\n\n");
        sb.AppendLine("| Task | Title | Depends on | Files | Risk | Verified by | Rationale |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var t in plan.Tasks)
            sb.AppendLine($"| `{t.Id}` | {t.Title} | {(t.DependsOn.Count == 0 ? "—" : string.Join(", ", t.DependsOn))} | {string.Join(", ", t.Files.Select(f => $"`{f}`"))} | {t.Risk} | {string.Join(", ", t.VerifiedBy)} | {t.Rationale} |");
        if (plan.AlreadySatisfied.Count > 0)
        {
            sb.AppendLine("\n**No change needed:**");
            foreach (var s in plan.AlreadySatisfied)
                sb.AppendLine($"- {s}");
        }
        return sb.ToString();
    }
}
