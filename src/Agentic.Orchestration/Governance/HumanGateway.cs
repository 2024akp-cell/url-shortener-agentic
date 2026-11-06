using Agentic.Orchestration.Agents;

namespace Agentic.Orchestration.Governance;

public enum ApprovalOutcome
{
    Approved,
    ApprovedWithChanges,
    Rejected,
}

public sealed record ApprovalRequest(
    string RunId,
    string StageId,
    string StageName,
    int Attempt,
    string Reason,
    string Summary,
    IReadOnlyList<PolicyFinding> Findings,
    string? Diff,
    IReadOnlyDictionary<string, object> Outputs);

/// <summary>
/// A human decision. ApprovedWithChanges carries amended artifacts (key → new content); the engine
/// records them as new versions authored by the approver, which triggers re-planning downstream.
/// </summary>
public sealed record ApprovalDecision(
    ApprovalOutcome Outcome,
    string Approver,
    string Comment,
    IReadOnlyDictionary<string, object>? Amendments = null)
{
    public static ApprovalDecision Approve(string approver, string comment = "") => new(ApprovalOutcome.Approved, approver, comment);

    public static ApprovalDecision Reject(string approver, string comment) => new(ApprovalOutcome.Rejected, approver, comment);
}

public sealed record ClarificationAnswers(string Respondent, IReadOnlyDictionary<string, string> Answers);

/// <summary>The only channel through which the system obtains human authority.</summary>
public interface IHumanGateway
{
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct);

    Task<ClarificationAnswers> RequestClarificationAsync(
        string runId, string stageId, IReadOnlyList<ClarificationQuestion> questions, CancellationToken ct);
}

/// <summary>Safe default for unattended runs: never approves anything.</summary>
public sealed class DenyAllHumanGateway : IHumanGateway
{
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct) =>
        Task.FromResult(ApprovalDecision.Reject("policy:unattended", "No human available; high-impact actions are denied by default."));

    public Task<ClarificationAnswers> RequestClarificationAsync(
        string runId, string stageId, IReadOnlyList<ClarificationQuestion> questions, CancellationToken ct) =>
        Task.FromResult(new ClarificationAnswers("policy:unattended", new Dictionary<string, string>()));
}
