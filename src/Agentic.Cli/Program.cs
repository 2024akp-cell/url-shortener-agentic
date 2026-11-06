using System.Text.Json;
using Agentic.Agents;
using Agentic.Agents.Model;
using Agentic.Cli;
using Agentic.Orchestration.Context;
using Agentic.Orchestration.Engine;
using Agentic.Orchestration.Governance;
using Agentic.Orchestration.Observability;
using Agentic.Orchestration.Workspace;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var repoRoot = FindRepoRoot();
var runsRoot = Path.Combine(repoRoot, "runs");

return args.FirstOrDefault()?.ToLowerInvariant() switch
{
    "run" when args.Length >= 2 => await RunAsync(args[1], args.Skip(2).ToArray()),
    "metrics" => ShowMetrics(),
    "verify-audit" when args.Length >= 2 => VerifyAudit(args[1]),
    _ => Usage(),
};

async Task<int> RunAsync(string scenarioName, string[] options)
{
    var scenario = Scenarios.Find(scenarioName);
    if (scenario is null)
        return Usage();

    var autoApprove = options.Contains("--auto-approve");
    var runId = $"{scenario.Name}-{DateTime.Now:yyyyMMdd-HHmmss}";
    var runDir = Path.Combine(runsRoot, runId);
    // Sandboxes live under a short temp path: build output under deep repo paths exceeds Windows MAX_PATH.
    var workspace = new SandboxWorkspace(Path.Combine(Path.GetTempPath(), "agentic", runId));
    if (scenario.SeedExistingCodebase)
    {
        workspace.SeedFrom(repoRoot, Scenarios.ServiceFiles(repoRoot));
        scenario.Setup?.Invoke(workspace);
    }

    var consoleLock = new object();
    IHumanGateway human = autoApprove
        ? new ScriptedHumanGateway(scenario.ScriptedAnswers, scenario.ScriptedRule)
        : Console.IsInputRedirected ? new DenyAllHumanGateway() : new ConsoleHumanGateway(consoleLock);

    var governance = new GovernanceOptions { KillSwitchPath = Path.Combine(runDir, "STOP") };
    var graph = SdlcWorkflow.Create(scenario.Name, existingCodebase: scenario.SeedExistingCodebase);
    var session = new RunSession(runId, scenario.Name, runDir, graph, workspace, TimeProvider.System);
    foreach (var fault in options.Where(o => o.StartsWith("--chaos=", StringComparison.Ordinal)))
    {
        var parts = fault["--chaos=".Length..].Split(':');
        session.InjectedFaults.Add((parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 1));
    }

    using var audit = new AuditLog(Path.Combine(runDir, "audit.jsonl"));
    session.Bus.Subscribe(audit);
    session.Bus.Subscribe(new ConsoleReporter(consoleLock));
    session.Context.Put(Keys.Requirement, scenario.Requirement, "human:requester", 0, Array.Empty<ArtifactRef>());

    Console.WriteLine($"Scenario : {scenario.Name} — {scenario.Description}");
    Console.WriteLine($"Run      : {runId}");
    Console.WriteLine($"Approvals: {(autoApprove ? "scripted reviewer (--auto-approve)" : human is DenyAllHumanGateway ? "none available → high-impact actions denied" : "interactive")}");
    Console.WriteLine($"Kill     : create {governance.KillSwitchPath} to safe-stop");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var orchestrator = new Orchestrator(SdlcWorkflow.CreateAgents(repoRoot), PolicyEngine.Default(governance), human, governance);
    await orchestrator.RunAsync(session, cts.Token);

    var report = RunReportWriter.Write(session, scenario.Requirement);
    var metrics = session.MetricsSnapshot();
    File.AppendAllText(Path.Combine(runsRoot, "metrics-history.jsonl"), JsonSerializer.Serialize(metrics with { Timeline = Array.Empty<StageTiming>() }, Json.Compact) + Environment.NewLine);

    Console.WriteLine();
    Console.ForegroundColor = session.Succeeded ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine(session.Succeeded ? "RESULT: succeeded" : $"RESULT: halted — {session.HaltReason}");
    Console.ResetColor();
    Console.WriteLine($"latency {metrics.EndToEndLatencyMs / 1000:0.0}s | attempts {metrics.Attempts} ({metrics.AttemptSuccessRate:P0} ok) | retries {metrics.Retries} | fallbacks {metrics.Fallbacks} | rollbacks {metrics.Rollbacks} | re-plans {metrics.Replans} | MTTR {(metrics.MttrMs is { } m ? $"{m / 1000:0.0}s" : "n/a")}");
    Console.WriteLine($"Report   : {report}");
    Console.WriteLine($"Diff     : {Path.Combine(runDir, "changes.diff")}");
    return session.Succeeded ? 0 : 2;
}

int ShowMetrics()
{
    var path = Path.Combine(runsRoot, "metrics-history.jsonl");
    if (!File.Exists(path))
    {
        Console.WriteLine("No runs recorded yet.");
        return 0;
    }
    var runs = File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<RunMetrics>(l, Json.Options)!).ToList();
    var latencies = runs.Select(r => r.EndToEndLatencyMs).OrderBy(x => x).ToList();
    double Percentile(double p) => latencies[(int)Math.Min(latencies.Count - 1, Math.Ceiling(p * latencies.Count) - 1)];
    var mttrs = runs.Where(r => r.MttrMs is not null).Select(r => r.MttrMs!.Value).ToList();

    Console.WriteLine($"Runs                      : {runs.Count}");
    Console.WriteLine($"Run success rate          : {(double)runs.Count(r => r.Outcome == "succeeded") / runs.Count:P0}");
    Console.WriteLine($"Attempt success rate      : {(double)runs.Sum(r => r.SuccessfulAttempts) / Math.Max(1, runs.Sum(r => r.Attempts)):P0}");
    Console.WriteLine($"Retries per run           : {runs.Average(r => r.Retries + r.FeedbackLoops):0.00}");
    Console.WriteLine($"Rollbacks per run         : {runs.Average(r => r.Rollbacks):0.00}");
    Console.WriteLine($"Fallbacks per run         : {runs.Average(r => r.Fallbacks):0.00}");
    Console.WriteLine($"MTTR (mean)               : {(mttrs.Count == 0 ? "n/a" : $"{mttrs.Average() / 1000:0.0}s")}");
    Console.WriteLine($"E2E latency p50 / p95     : {Percentile(0.5) / 1000:0.0}s / {Percentile(0.95) / 1000:0.0}s");
    Console.WriteLine();
    Console.WriteLine($"{"run",-34} {"outcome",-10} {"latency",8} {"retry",5} {"rollbk",6} {"replan",6} {"deny",4}");
    foreach (var r in runs.TakeLast(15))
        Console.WriteLine($"{r.RunId,-34} {r.Outcome,-10} {r.EndToEndLatencyMs / 1000,7:0.0}s {r.Retries + r.FeedbackLoops,5} {r.Rollbacks,6} {r.Replans,6} {r.PolicyDenials,4}");
    return 0;
}

int VerifyAudit(string runIdOrPath)
{
    var path = File.Exists(runIdOrPath) ? runIdOrPath : Path.Combine(runsRoot, runIdOrPath, "audit.jsonl");
    if (!File.Exists(path))
    {
        Console.WriteLine($"Audit log not found: {path}");
        return 1;
    }
    var result = AuditLog.Verify(path);
    Console.ForegroundColor = result.Valid ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"{(result.Valid ? "VALID" : "TAMPERED")}: {result.Message}");
    Console.ResetColor();
    return result.Valid ? 0 : 3;
}

int Usage()
{
    Console.WriteLine("""
        Agentic SDLC orchestrator — URL shortener

        Usage:
          agentic run <scenario> [--auto-approve] [--chaos=<stageId>[:attempt]]
          agentic metrics
          agentic verify-audit <runId>

        Scenarios:
        """);
    foreach (var s in Scenarios.All)
        Console.WriteLine($"  {s.Name,-11} {s.Description}");
    return 1;
}

static string FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "UrlShortenerAgentic.slnx")))
                return dir.FullName;
    throw new InvalidOperationException("Run from inside the repository (UrlShortenerAgentic.slnx not found).");
}
