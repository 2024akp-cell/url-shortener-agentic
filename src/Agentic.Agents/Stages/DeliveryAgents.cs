using System.Text;
using System.Text.RegularExpressions;
using Agentic.Agents.Knowledge;
using Agentic.Agents.Model;
using Agentic.Agents.Tooling;
using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Context;
using Agentic.Orchestration.Governance;
using Agentic.Orchestration.Workspace;

namespace Agentic.Agents.Stages;

/// <summary>
/// Proposes a change set for one planned task. Each retry or downstream failure moves it to the next
/// candidate strategy (self-correction with feedback); the conservative variant is the fallback agent.
/// </summary>
public sealed class ImplementationAgent : IAgent
{
    private readonly ChangeCatalog _catalog;
    private readonly bool _conservative;

    public ImplementationAgent(ChangeCatalog catalog, bool conservative = false)
    {
        _catalog = catalog;
        _conservative = conservative;
    }

    public string Id => _conservative ? "implementation.conservative" : "implementation";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var taskId = ctx.Stage.Parameter("task") ?? throw new InvalidOperationException("Stage has no task parameter.");
        var spec = ctx.Context.Require<RequirementSpec>(Keys.Requirements);
        var candidates = _catalog.For(taskId);

        ChangeCandidate? candidate;
        if (_conservative)
            candidate = candidates.LastOrDefault(c => c.Conservative);
        else
        {
            // Primary agent never proposes the conservative-only strategy when a fallback agent owns it.
            var primary = ctx.Stage.FallbackAgentId is null ? candidates : candidates.Where(c => !c.Conservative).ToList();
            candidate = ctx.Feedback.Count < primary.Count ? primary[ctx.Feedback.Count] : null;
        }
        if (candidate is null)
            return Task.FromResult(AgentResult.Failed($"No further implementation strategy available for task '{taskId}'."));

        ChangeSet changes;
        try
        {
            changes = candidate.Build(ctx.Workspace, spec);
        }
        catch (PatchConflictException ex)
        {
            return Task.FromResult(AgentResult.Failed(ex.Message));
        }

        var why = ctx.Feedback.Count == 0
            ? candidate.Rationale
            : $"{candidate.Rationale} Previous attempt failed: {ctx.Feedback[^1].Reason}";
        ctx.Log($"Strategy: {candidate.Strategy} ({changes.Changes.Count} file(s))");
        return Task.FromResult(new AgentResult
        {
            Summary = $"{candidate.Strategy} — {changes.Changes.Count} file(s)",
            Changes = changes,
            Decisions =
            {
                new DecisionDraft(DecisionKind.Implementation, $"Task {taskId}: {candidate.Strategy}", why,
                    candidates.Where(c => c != candidate).Select(c => c.Strategy).ToList()),
            },
        });
    }
}

/// <summary>Reviews the cumulative diff against security and compliance rules before release.</summary>
public sealed partial class SecurityReviewAgent : IAgent
{
    public string Id => "security-review";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var findings = new List<PolicyFinding>();
        foreach (var (path, original, current) in ctx.Workspace.Changes())
        {
            if (current is null)
                continue;
            var before = new HashSet<string>((original ?? "").Replace("\r\n", "\n").Split('\n'));
            var lines = current.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (before.Contains(lines[i]))
                    continue;
                findings.AddRange(SecretScanPolicy.Scan(path, i + 1, lines[i]));
                findings.AddRange(PiiLoggingPolicy.Scan(path, i + 1, lines[i]));
            }
            // Authorization review: any management write endpoint must sit behind the API-key filter.
            if (path.EndsWith(".cs", StringComparison.Ordinal) && WriteEndpoint().IsMatch(current) && !current.Contains("ApiKeyFilter", StringComparison.Ordinal))
                findings.Add(new PolicyFinding("SEC-003-authz", "security", PolicyVerdict.Deny, "Write endpoint without ApiKeyFilter.", path));
        }

        var passed = findings.All(f => f.Verdict != PolicyVerdict.Deny);
        var sb = new StringBuilder("## 6. Security & compliance review\n\n");
        sb.AppendLine($"- Files reviewed: {ctx.Workspace.Changes().Count}");
        sb.AppendLine("- Checks: secrets, PII in logs, authorization on write endpoints (plus per-change guardrails enforced at apply time)");
        sb.AppendLine(findings.Count == 0 ? "- Result: **no findings**" : string.Join("\n", findings.Select(f => $"- {f.Verdict}: [{f.PolicyId}] {f.Message} `{f.Path}:{f.Line}`")));

        var result = new AgentResult
        {
            Success = passed,
            Error = passed ? null : "Security review found blocking issues.",
            Summary = passed ? $"Passed ({findings.Count} non-blocking findings)" : "Blocking security findings",
            Outputs = { [Keys.SecurityReview] = findings, [Keys.SecurityReviewDoc] = sb.ToString() },
        };
        return Task.FromResult(result);
    }

    [GeneratedRegex("\\.Map(Post|Put|Delete|Patch)\\(")]
    private static partial Regex WriteEndpoint();
}

/// <summary>Keeps documentation in step with the code: changelog entry and generated API reference.</summary>
public sealed partial class DocumentationAgent : IAgent
{
    public string Id => "documentation";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var spec = ctx.Context.Require<RequirementSpec>(Keys.Requirements);
        var plan = ctx.Context.Require<TaskPlan>(Keys.Plan);
        var ws = ctx.Workspace;

        var api = new StringBuilder("# API reference\n\n_Generated from source by the documentation agent._\n\n| Method | Route | Source |\n|---|---|---|\n");
        foreach (var path in ws.ListFiles(".cs").Where(p => p.Contains("/Endpoints/", StringComparison.Ordinal)))
        {
            foreach (var (method, route) in RouteScanner.Scan(ws.ReadText(path)))
                api.AppendLine($"| {method} | `{route}` | `{path}` |");
        }

        var existing = ws.TryReadText("CHANGELOG.md") ?? "# Changelog\n";
        var entry = new StringBuilder($"\n## [Unreleased] — {ctx.Time.GetUtcNow():yyyy-MM-dd}\n\n");
        foreach (var task in plan.Tasks)
            entry.AppendLine($"- {(spec.Kind == Model.ChangeKind.Greenfield ? "Added" : task.Id.StartsWith("fix", StringComparison.Ordinal) ? "Fixed" : "Changed")}: {task.Title}");
        var changelog = existing.TrimEnd() + "\n" + entry;

        return Task.FromResult(new AgentResult
        {
            Summary = "Updated CHANGELOG.md and docs/API.md",
            Changes = new ChangeSet("Documentation update", new[]
            {
                FileChange.Write("docs/API.md", api.ToString()),
                FileChange.Write("CHANGELOG.md", changelog),
            }),
            Outputs = { [Keys.Docs] = new[] { "docs/API.md", "CHANGELOG.md" } },
        });
    }

}

/// <summary>Go/no-go assessment. The release itself always requires human sign-off.</summary>
public sealed class ReleaseReadinessAgent : IAgent
{
    public string Id => "release-readiness";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var spec = ctx.Context.Require<RequirementSpec>(Keys.Requirements);
        var plan = ctx.Context.Require<TaskPlan>(Keys.Plan);
        var tests = ctx.Context.Get<TestReport>(Keys.TestReport);
        var security = ctx.Context.Get<List<PolicyFinding>>(Keys.SecurityReview);
        var docs = ctx.Context.Get<string[]>(Keys.Docs);

        var untraced = plan.Tasks.Where(t => t.VerifiedBy.Count == 0 && t.Id != "scaffold" && t.Id != "tests").Select(t => t.Id).ToList();
        var checklist = new List<ReleaseChecklistItem>
        {
            new("Build and full test suite green", tests is { BuildSucceeded: true, Failed: 0 }, tests is null ? "no test report" : $"{tests.Passed}/{tests.Total} passed"),
            new("Security & compliance review passed", security is not null && security.All(f => f.Verdict != PolicyVerdict.Deny), $"{security?.Count ?? 0} findings"),
            new("Documentation updated", docs is { Length: > 0 }, docs is null ? "missing" : string.Join(", ", docs)),
            new("Every task traced to verifying tests", untraced.Count == 0, untraced.Count == 0 ? "all tasks have VerifiedBy" : "untraced: " + string.Join(", ", untraced)),
            new("No unresolved blocking ambiguity", spec.Ambiguities.All(a => a.Severity != AmbiguitySeverity.Blocking || a.ResolvedBy == "human"),
                $"{spec.Ambiguities.Count(a => a.ResolvedBy == "human")} resolved by human, {spec.Assumptions.Count} assumptions"),
        };
        var openRisks = spec.Risks.Where(r => r.Impact == "High").ToList();
        var go = checklist.All(c => c.Passed);

        var sb = new StringBuilder("## 7. Release readiness\n\n");
        sb.AppendLine($"**Recommendation: {(go ? "GO" : "NO-GO")}** (final decision is the human approver's)\n");
        sb.AppendLine("| Check | Result | Evidence |");
        sb.AppendLine("|---|---|---|");
        foreach (var c in checklist)
            sb.AppendLine($"| {c.Check} | {(c.Passed ? "✅" : "❌")} | {c.Evidence} |");
        if (openRisks.Count > 0)
        {
            sb.AppendLine("\n**High-impact risks accepted with mitigation:**");
            foreach (var r in openRisks)
                sb.AppendLine($"- {r.Id}: {r.Description} → {r.Mitigation}");
        }
        if (spec.Assumptions.Count > 0)
        {
            sb.AppendLine("\n**Assumptions carried into release:**");
            foreach (var a in spec.Assumptions)
                sb.AppendLine($"- {a}");
        }

        return Task.FromResult(new AgentResult
        {
            Success = go,
            Error = go ? null : "Release checklist not satisfied.",
            Summary = go ? "GO: all release checks passed" : "NO-GO: " + string.Join("; ", checklist.Where(c => !c.Passed).Select(c => c.Check)),
            Outputs = { [Keys.ReleaseReadiness] = new ReleaseReadiness(go, checklist, openRisks), [Keys.ReleaseReadinessDoc] = sb.ToString() },
            Decisions = { new DecisionDraft(DecisionKind.Release, go ? "Recommend GO" : "Recommend NO-GO", $"{checklist.Count(c => c.Passed)}/{checklist.Count} checks passed.") },
        });
    }
}
