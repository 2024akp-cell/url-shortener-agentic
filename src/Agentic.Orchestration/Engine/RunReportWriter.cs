using System.Text;
using System.Text.Json;
using Agentic.Orchestration.Context;
using Agentic.Orchestration.Observability;
using Agentic.Orchestration.Workspace;

namespace Agentic.Orchestration.Engine;

/// <summary>Writes the reviewable outcome of a run: report, metrics, decisions, artifacts, diff and graph.</summary>
public static class RunReportWriter
{
    public static string Write(RunSession session, string requirement)
    {
        var dir = session.RunDirectory;
        Directory.CreateDirectory(dir);
        var metrics = session.MetricsSnapshot();

        session.Context.Persist(Path.Combine(dir, "artifacts"));
        File.WriteAllText(Path.Combine(dir, "metrics.json"), JsonSerializer.Serialize(metrics, Json.Options));
        File.WriteAllText(Path.Combine(dir, "decisions.json"), JsonSerializer.Serialize(session.Decisions.All(), Json.Options));
        File.WriteAllText(Path.Combine(dir, "stages.json"), JsonSerializer.Serialize(session.Stages, Json.Options));
        File.WriteAllText(Path.Combine(dir, "changes.diff"), session.Workspace.UnifiedDiff());
        File.WriteAllText(Path.Combine(dir, "graph.mmd"), Mermaid(session));

        var reportPath = Path.Combine(dir, "report.md");
        File.WriteAllText(reportPath, BuildReport(session, requirement, metrics));
        return reportPath;
    }

    private static string Mermaid(RunSession session) =>
        session.Graph.ToMermaid(session.Stages.ToDictionary(s => s.StageId, s => $"{s.Status} x{s.Attempts}"));

    private static string BuildReport(RunSession session, string requirement, RunMetrics m)
    {
        var sb = new StringBuilder();
        var events = session.Bus.Events;
        sb.AppendLine($"# Run report — {session.Scenario}");
        sb.AppendLine();
        sb.AppendLine($"- **Run id:** `{session.RunId}`");
        sb.AppendLine($"- **Outcome:** {(session.Succeeded ? "✅ succeeded" : "⛔ halted (safe-stop)")}");
        if (session.HaltReason is not null)
            sb.AppendLine($"- **Halt reason:** {session.HaltReason}");
        sb.AppendLine($"- **Workspace:** `{session.Workspace.Root}`");
        sb.AppendLine();
        sb.AppendLine("## Requirement (as received)");
        sb.AppendLine();
        sb.AppendLine("> " + requirement.Replace("\n", "\n> "));
        sb.AppendLine();

        // Agent-authored narrative sections (requirements analysis, design, plan, test & release reports).
        foreach (var key in new[] { "requirements-doc", "codebase-analysis-doc", "design-doc", "plan-doc", "test-report-doc", "security-review-doc", "release-readiness-doc" })
            if (session.Context.Latest(key) is { Content: string text } artifact)
            {
                sb.AppendLine(text.TrimEnd());
                sb.AppendLine();
                sb.AppendLine($"<sub>Source: {artifact.Ref} produced by `{artifact.ProducedBy}` from {Lineage(artifact)}</sub>");
                sb.AppendLine();
            }

        sb.AppendLine("## Orchestration graph (final)");
        sb.AppendLine();
        sb.AppendLine("```mermaid");
        sb.Append(Mermaid(session));
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Stage results");
        sb.AppendLine();
        sb.AppendLine("| Stage | Status | Attempts | Fallback | Summary |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var s in session.Stages)
            sb.AppendLine($"| {s.Name} (`{s.StageId}`) | {s.Status} | {s.Attempts} | {(s.UsedFallback ? "yes" : "")} | {Cell(s.Summary ?? s.LastError)} |");
        sb.AppendLine();

        sb.AppendLine("## Execution timeline");
        sb.AppendLine();
        sb.AppendLine("| Stage | Attempt | Start (ms) | Duration (ms) | Outcome |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var t in m.Timeline.OrderBy(t => t.Start))
            sb.AppendLine($"| `{t.StageId}` | {t.Attempt} | {(t.Start - m.StartedAt).TotalMilliseconds:0} | {t.DurationMs:0} | {t.Outcome} |");
        sb.AppendLine();

        sb.AppendLine("## Control-flow events (retries, rollbacks, re-plans, safe-stop)");
        sb.AppendLine();
        var control = events.Where(e => e.Type.StartsWith("control.", StringComparison.Ordinal) || e.Type is EventTypes.GraphPatched or EventTypes.StageInvalidated).ToList();
        if (control.Count == 0)
            sb.AppendLine("_None — every stage passed first time._");
        foreach (var e in control)
            sb.AppendLine($"- `{e.Type}` {(e.StageId is null ? "" : $"**{e.StageId}** ")}{e.Message}");
        sb.AppendLine();

        sb.AppendLine("## Human checkpoints");
        sb.AppendLine();
        var human = events.Where(e => e.Type is EventTypes.ApprovalGranted or EventTypes.ApprovalRejected or EventTypes.ClarificationAnswered).ToList();
        if (human.Count == 0)
            sb.AppendLine("_No human interaction was required._");
        foreach (var e in human)
            sb.AppendLine($"- **{e.StageId}** — {e.Actor}: {e.Message}");
        sb.AppendLine();

        sb.AppendLine("## Policy guardrail findings");
        sb.AppendLine();
        var findings = events.Where(e => e.Type == EventTypes.PolicyFinding).ToList();
        if (findings.Count == 0)
            sb.AppendLine("_No findings._");
        foreach (var e in findings)
            sb.AppendLine($"- `{e.Actor}` on **{e.StageId}** (attempt {e.Attempt}): {e.Message} {(e.Data.GetValueOrDefault("path") is { } p ? $"— `{p}`" : "")}");
        sb.AppendLine();

        sb.AppendLine("## Decision lineage");
        sb.AppendLine();
        sb.AppendLine("| Id | Stage | Actor | Kind | Decision | Rationale | Based on |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var d in session.Decisions.All())
            sb.AppendLine($"| {d.Id} | {d.StageId} | {d.Actor} | {d.Kind} | {Cell(d.Summary)} | {Cell(d.Rationale)} | {string.Join(", ", d.BasedOn)} |");
        sb.AppendLine();

        sb.AppendLine("## Change summary (sandbox vs. starting state)");
        sb.AppendLine();
        var changes = session.Workspace.Changes();
        if (changes.Count == 0)
            sb.AppendLine("_No file changes._");
        else
        {
            sb.AppendLine("| File | Change | +/- |");
            sb.AppendLine("|---|---|---|");
            foreach (var (path, original, current) in changes)
            {
                var (added, removed) = DiffBuilder.Stats(original, current);
                var kind = original is null ? "added" : current is null ? "deleted" : "modified";
                sb.AppendLine($"| `{path}` | {kind} | +{added} / -{removed} |");
            }
            sb.AppendLine();
            sb.AppendLine("Full unified diff: `changes.diff`.");
        }
        sb.AppendLine();

        sb.AppendLine("## Reliability metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| End-to-end latency | {m.EndToEndLatencyMs / 1000:0.0} s (of which human wait {m.HumanWaitMs / 1000:0.0} s) |");
        sb.AppendLine($"| Attempt success rate | {m.AttemptSuccessRate:P0} ({m.SuccessfulAttempts}/{m.Attempts}) |");
        sb.AppendLine($"| Retries / fallbacks | {m.Retries} / {m.Fallbacks} |");
        sb.AppendLine($"| Rollbacks | {m.Rollbacks} |");
        sb.AppendLine($"| Feedback loops / re-plans | {m.FeedbackLoops} / {m.Replans} |");
        sb.AppendLine($"| Failure episodes (recovered) | {m.FailureEpisodes} ({m.RecoveredEpisodes}) |");
        sb.AppendLine($"| MTTR | {(m.MttrMs is { } mttr ? $"{mttr / 1000:0.00} s" : "n/a")} |");
        sb.AppendLine($"| Policy findings (denials) | {m.PolicyFindings} ({m.PolicyDenials}) |");
        sb.AppendLine($"| Approvals requested (rejected) | {m.ApprovalsRequested} ({m.ApprovalsRejected}) |");
        sb.AppendLine();

        sb.AppendLine("## Traceability");
        sb.AppendLine();
        sb.AppendLine("- `audit.jsonl` — hash-chained audit log of every event (verify with `agentic verify-audit <runId>`)");
        sb.AppendLine("- `artifacts/` — every artifact version with `.meta.json` lineage (producer, attempt, inputs, hash)");
        sb.AppendLine("- `decisions.json`, `stages.json`, `metrics.json`, `graph.mmd`, `changes.diff`");
        return sb.ToString();
    }

    private static string Lineage(Artifact artifact) =>
        artifact.DerivedFrom.Count == 0 ? "no upstream inputs" : string.Join(", ", artifact.DerivedFrom);

    private static string Cell(string? text) =>
        (text ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
