using Agentic.Orchestration.Workflow;

namespace Agentic.Orchestration.Governance;

/// <summary>The autonomy boundary for a run. Everything outside it requires a human.</summary>
public sealed class GovernanceOptions
{
    public int MaxParallelism { get; init; } = 4;

    /// <summary>Hard cap on agent attempts across the whole run (runaway-loop protection).</summary>
    public int MaxTotalAttempts { get; init; } = 40;

    public TimeSpan MaxRunDuration { get; init; } = TimeSpan.FromMinutes(30);

    public int MaxClarificationRounds { get; init; } = 2;

    /// <summary>Max times a downstream failure may re-open upstream stages.</summary>
    public int MaxFeedbackLoops { get; init; } = 3;

    public int MaxReplans { get; init; } = 3;

    /// <summary>Stages at or above this impact always need human approval.</summary>
    public ImpactLevel ApprovalThreshold { get; init; } = ImpactLevel.High;

    /// <summary>On safe-stop, undo every workspace change so the sandbox returns to its starting state.</summary>
    public bool RollbackWorkspaceOnHalt { get; init; }

    public IReadOnlyList<string> ProtectedPaths { get; init; } = new[] { "Program.cs", "*.csproj", "SqliteDatabase.cs", "Directory.Build.props" };

    public IReadOnlyList<string> WritablePathPrefixes { get; init; } = new[] { "src/", "tests/", "docs/", "README.md", "CHANGELOG.md", "Directory.Build.props" };

    public int MaxFilesPerChange { get; init; } = 12;

    public int MaxLinesPerChange { get; init; } = 600;

    public IReadOnlySet<string> AllowedPackages { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Data.Sqlite",
        "Microsoft.AspNetCore.OpenApi",
        "Microsoft.AspNetCore.Mvc.Testing",
        "Microsoft.NET.Test.Sdk",
        "xunit",
        "xunit.runner.visualstudio",
    };

    /// <summary>Optional kill-switch file; if it appears the run stops at the next safe point.</summary>
    public string? KillSwitchPath { get; init; }
}
