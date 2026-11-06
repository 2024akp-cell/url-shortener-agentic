using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Governance;
using Agentic.Orchestration.Observability;

namespace Agentic.Cli;

/// <summary>Interactive human checkpoint: the operator sees summary, guardrail findings and diff, then decides.</summary>
public sealed class ConsoleHumanGateway : IHumanGateway
{
    private readonly object _consoleLock;

    public ConsoleHumanGateway(object consoleLock) => _consoleLock = consoleLock;

    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        lock (_consoleLock)
        {
            Console.WriteLine();
            Write(ConsoleColor.Yellow, $"══ APPROVAL REQUIRED: {request.StageName} (attempt {request.Attempt}) ══");
            Console.WriteLine($"Why:     {request.Reason}");
            Console.WriteLine($"Summary: {request.Summary}");
            foreach (var f in request.Findings)
                Write(f.Verdict == PolicyVerdict.Deny ? ConsoleColor.Red : ConsoleColor.DarkYellow, $"  [{f.PolicyId}] {f.Verdict}: {f.Message} {f.Path}");
            if (!string.IsNullOrEmpty(request.Diff))
            {
                var lines = request.Diff.Split('\n');
                foreach (var line in lines.Take(40))
                    Write(line.StartsWith('+') ? ConsoleColor.Green : line.StartsWith('-') ? ConsoleColor.Red : ConsoleColor.Gray, "  " + line);
                if (lines.Length > 40)
                    Console.WriteLine($"  ... {lines.Length - 40} more diff lines (see run folder)");
            }

            var user = Environment.UserName;
            while (true)
            {
                Console.Write("Decision [a]pprove / [r]eject: ");
                var key = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (key is "a" or "approve")
                    return Task.FromResult(ApprovalDecision.Approve(user, "Approved interactively."));
                if (key is "r" or "reject")
                {
                    Console.Write("Reason: ");
                    return Task.FromResult(ApprovalDecision.Reject(user, Console.ReadLine() ?? "rejected"));
                }
            }
        }
    }

    public Task<ClarificationAnswers> RequestClarificationAsync(string runId, string stageId, IReadOnlyList<ClarificationQuestion> questions, CancellationToken ct)
    {
        lock (_consoleLock)
        {
            Console.WriteLine();
            Write(ConsoleColor.Cyan, "══ CLARIFICATION NEEDED ══");
            var answers = new Dictionary<string, string>();
            foreach (var q in questions)
            {
                Console.WriteLine($"{q.Id}: {q.Question}");
                Console.WriteLine($"  why: {q.WhyItMatters}");
                Console.Write($"  answer [Enter = \"{q.SuggestedDefault}\"]: ");
                var answer = Console.ReadLine();
                answers[q.Id] = string.IsNullOrWhiteSpace(answer) ? q.SuggestedDefault ?? "" : answer.Trim();
            }
            return Task.FromResult(new ClarificationAnswers(Environment.UserName, answers));
        }
    }

    private static void Write(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}

/// <summary>Live, human-readable view of the event stream.</summary>
public sealed class ConsoleReporter : IRunObserver
{
    private readonly object _consoleLock;
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    public ConsoleReporter(object consoleLock) => _consoleLock = consoleLock;

    public void OnEvent(RunEvent e)
    {
        var (color, icon) = e.Type switch
        {
            EventTypes.RunStarted or EventTypes.RunCompleted => (ConsoleColor.Green, "■"),
            EventTypes.RunHalted or EventTypes.SafeStop => (ConsoleColor.Red, "■"),
            EventTypes.StageStarted => (ConsoleColor.White, "▶"),
            EventTypes.StageSucceeded => (ConsoleColor.Green, "✔"),
            EventTypes.AttemptFailed or EventTypes.StageFailed or EventTypes.GateFailed => (ConsoleColor.Red, "✖"),
            EventTypes.Retry or EventTypes.Fallback or EventTypes.FeedbackLoop or EventTypes.Rollback => (ConsoleColor.Magenta, "↺"),
            EventTypes.Replan or EventTypes.GraphPatched or EventTypes.StageInvalidated => (ConsoleColor.Blue, "⟳"),
            EventTypes.PolicyFinding => (ConsoleColor.DarkYellow, "⚑"),
            EventTypes.ApprovalGranted or EventTypes.ClarificationAnswered => (ConsoleColor.Cyan, "☑"),
            EventTypes.ApprovalRejected => (ConsoleColor.Red, "☒"),
            EventTypes.ApprovalRequested or EventTypes.ClarificationRequested => (ConsoleColor.Yellow, "?"),
            EventTypes.AgentLog => (ConsoleColor.DarkGray, "·"),
            EventTypes.Decision => (ConsoleColor.DarkCyan, "◆"),
            _ => ((ConsoleColor?)null, (string?)null),
        };
        if (icon is null || color is null)
            return;

        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"{(e.At - _start).TotalSeconds,6:0.0}s ");
            Console.ForegroundColor = color.Value;
            var stage = e.StageId is null ? "" : $"[{e.StageId}{(e.Attempt is { } a ? $"#{a}" : "")}] ";
            Console.WriteLine($"{icon} {stage}{e.Message}");
            Console.ResetColor();
        }
    }
}
