using Agentic.Orchestration.Context;
using Agentic.Orchestration.Workspace;

namespace Agentic.Orchestration.Workflow;

public sealed record GateResult(bool Passed, string Reason)
{
    public static GateResult Pass(string reason = "ok") => new(true, reason);

    public static GateResult Fail(string reason) => new(false, reason);
}

/// <summary>Read-only view a gate evaluates against. Exit gates also see the stage's uncommitted outputs.</summary>
public sealed record GateContext(
    StageDefinition Stage,
    ContextStore Context,
    SandboxWorkspace Workspace,
    IReadOnlyDictionary<string, object> PendingOutputs)
{
    public T? Output<T>(string key) where T : class =>
        PendingOutputs.TryGetValue(key, out var value) ? value as T : Context.Get<T>(key);
}

public interface IGate
{
    string Name { get; }

    GateResult Evaluate(GateContext context);
}

public static class Gates
{
    /// <summary>Entry gate: required inputs exist.</summary>
    public static IGate ArtifactsPresent(params string[] keys) => new DelegateGate(
        $"artifacts-present({string.Join(",", keys)})",
        ctx =>
        {
            var missing = keys.Where(k => ctx.Context.Latest(k) is null && !ctx.PendingOutputs.ContainsKey(k)).ToList();
            return missing.Count == 0 ? GateResult.Pass() : GateResult.Fail($"missing artifacts: {string.Join(", ", missing)}");
        });

    /// <summary>Exit gate: the stage produced everything it declared.</summary>
    public static IGate ProducesDeclaredOutputs() => new DelegateGate(
        "declared-outputs-produced",
        ctx =>
        {
            var missing = ctx.Stage.Produces.Where(k => !ctx.PendingOutputs.ContainsKey(k)).ToList();
            return missing.Count == 0 ? GateResult.Pass() : GateResult.Fail($"stage did not produce: {string.Join(", ", missing)}");
        });

    public static IGate WorkspaceFilesExist(params string[] paths) => new DelegateGate(
        "workspace-files-exist",
        ctx =>
        {
            var missing = paths.Where(p => !ctx.Workspace.Exists(p)).ToList();
            return missing.Count == 0 ? GateResult.Pass() : GateResult.Fail($"missing files: {string.Join(", ", missing)}");
        });

    public static IGate Custom(string name, Func<GateContext, GateResult> evaluate) => new DelegateGate(name, evaluate);

    private sealed class DelegateGate : IGate
    {
        private readonly Func<GateContext, GateResult> _evaluate;

        public DelegateGate(string name, Func<GateContext, GateResult> evaluate)
        {
            Name = name;
            _evaluate = evaluate;
        }

        public string Name { get; }

        public GateResult Evaluate(GateContext context) => _evaluate(context);
    }
}
