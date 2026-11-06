using Agentic.Orchestration.Agents;

namespace Agentic.Orchestration.Governance;

/// <summary>
/// Replays pre-recorded reviewer decisions for repeatable demos and tests. The approver is always
/// named "scripted:*" so the audit trail never confuses a script with a real person.
/// </summary>
public sealed class ScriptedHumanGateway : IHumanGateway
{
    private readonly string _reviewer;
    private readonly IReadOnlyDictionary<string, string> _answers;
    private readonly Func<ApprovalRequest, int, ApprovalDecision?> _rule;
    private readonly Dictionary<string, int> _seen = new();

    /// <param name="rule">Given the request and how many times this stage asked before, return a decision (null = approve).</param>
    public ScriptedHumanGateway(
        IReadOnlyDictionary<string, string>? answers = null,
        Func<ApprovalRequest, int, ApprovalDecision?>? rule = null,
        string reviewer = "scripted:reviewer")
    {
        _reviewer = reviewer;
        _answers = answers ?? new Dictionary<string, string>();
        _rule = rule ?? ((_, _) => null);
    }

    public List<ApprovalRequest> Requests { get; } = new();

    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var nth = _seen.TryGetValue(request.StageId, out var n) ? n : 0;
        _seen[request.StageId] = nth + 1;
        var decision = _rule(request, nth) ?? ApprovalDecision.Approve(_reviewer, "Reviewed summary, findings and diff.");
        return Task.FromResult(decision with { Approver = _reviewer });
    }

    public Task<ClarificationAnswers> RequestClarificationAsync(
        string runId, string stageId, IReadOnlyList<ClarificationQuestion> questions, CancellationToken ct)
    {
        var answers = questions.Where(q => _answers.ContainsKey(q.Id)).ToDictionary(q => q.Id, q => _answers[q.Id]);
        return Task.FromResult(new ClarificationAnswers(_reviewer, answers));
    }
}
