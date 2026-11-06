namespace Agentic.Agents.Model;

/// <summary>Well-known artifact keys shared by agents and workflows.</summary>
public static class Keys
{
    public const string Requirement = "requirement";
    public const string Clarifications = "clarifications";
    public const string Requirements = "requirements";
    public const string RequirementsDoc = "requirements-doc";
    public const string CodebaseModel = "codebase-model";
    public const string CodebaseDoc = "codebase-analysis-doc";
    public const string BaselineTests = "baseline-test-report";
    public const string Design = "design";
    public const string DesignDoc = "design-doc";
    public const string Plan = "plan";
    public const string PlanDoc = "plan-doc";
    public const string TestReport = "test-report";
    public const string TestReportDoc = "test-report-doc";
    public const string SecurityReview = "security-review";
    public const string SecurityReviewDoc = "security-review-doc";
    public const string Docs = "docs";
    public const string ReleaseReadiness = "release-readiness";
    public const string ReleaseReadinessDoc = "release-readiness-doc";
}

public enum ChangeKind
{
    Greenfield,
    Brownfield,
}

public enum AmbiguitySeverity
{
    /// <summary>Agent may proceed on a documented assumption.</summary>
    Minor,

    /// <summary>Agent must not guess; a human must answer.</summary>
    Blocking,
}

public sealed record Ambiguity(string Id, string Phrase, AmbiguitySeverity Severity, string Question, string Resolution, string ResolvedBy);

public sealed record AcceptanceCriterion(string Id, string Capability, string Given, string When, string Then);

public sealed record Risk(string Id, string Description, string Likelihood, string Impact, string Mitigation);

public sealed record RequirementSpec(
    string Title,
    ChangeKind Kind,
    string Intent,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> FunctionalRequirements,
    IReadOnlyList<string> NonFunctionalRequirements,
    IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria,
    IReadOnlyList<Ambiguity> Ambiguities,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<Risk> Risks);

public sealed record CodeFile(string Path, string Project, string Layer, IReadOnlyList<string> Types, int Lines);

public sealed record Endpoint(string Method, string Route, string File);

public sealed record TestCase(string Class, string Method, string File);

public sealed record CodebaseModel(
    IReadOnlyList<string> Projects,
    IReadOnlyList<CodeFile> Files,
    IReadOnlyList<Endpoint> Endpoints,
    IReadOnlyList<TestCase> Tests,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectReferences);

public sealed record ApiOperation(string Method, string Route, string Purpose, string Responses);

public sealed record DesignSpec(
    IReadOnlyList<string> Components,
    IReadOnlyList<ApiOperation> Api,
    IReadOnlyList<string> DataModel,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> ImpactedFiles);

public sealed record PlannedTask(
    string Id,
    string Title,
    string Capability,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Files,
    string Risk,
    IReadOnlyList<string> VerifiedBy,
    string Rationale);

public sealed record TaskPlan(IReadOnlyList<PlannedTask> Tasks, IReadOnlyList<string> AlreadySatisfied)
{
    public static string StageIdFor(string taskId) => $"impl-{taskId}";
}

public sealed record TestReport(
    bool BuildSucceeded,
    int Total,
    int Passed,
    int Failed,
    IReadOnlyList<string> FailedTests,
    IReadOnlyList<string> BuildErrors,
    double DurationSeconds,
    string Command);

public sealed record ReleaseChecklistItem(string Check, bool Passed, string Evidence);

public sealed record ReleaseReadiness(bool Go, IReadOnlyList<ReleaseChecklistItem> Checklist, IReadOnlyList<Risk> OpenRisks);
