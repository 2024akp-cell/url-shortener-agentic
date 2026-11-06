using System.Text.RegularExpressions;
using Agentic.Orchestration.Workflow;
using Agentic.Orchestration.Workspace;

namespace Agentic.Orchestration.Governance;

public enum PolicyVerdict
{
    Allow,
    RequireApproval,
    Deny,
}

public sealed record PolicyFinding(string PolicyId, string Category, PolicyVerdict Verdict, string Message, string? Path = null, int? Line = null);

public sealed record PolicyContext(StageDefinition Stage, ChangeSet Changes, SandboxWorkspace Workspace);

/// <summary>A guardrail evaluated against every proposed change set before it touches the workspace.</summary>
public interface IPolicy
{
    string Id { get; }

    string Category { get; }

    IEnumerable<PolicyFinding> Evaluate(PolicyContext context);
}

public sealed class PolicyEngine
{
    private readonly IReadOnlyList<IPolicy> _policies;

    public PolicyEngine(IEnumerable<IPolicy> policies) => _policies = policies.ToList();

    public IReadOnlyList<IPolicy> Policies => _policies;

    public IReadOnlyList<PolicyFinding> Evaluate(PolicyContext context) =>
        _policies.SelectMany(p => p.Evaluate(context)).ToList();

    public static PolicyEngine Default(GovernanceOptions options) => new(new IPolicy[]
    {
        new SecretScanPolicy(),
        new PiiLoggingPolicy(),
        new ProtectedPathPolicy(options.ProtectedPaths),
        new ChangeSizePolicy(options.MaxFilesPerChange, options.MaxLinesPerChange),
        new DependencyAllowListPolicy(options.AllowedPackages),
        new ScopePolicy(options.WritablePathPrefixes),
    });
}

internal static class LineScanner
{
    public static IEnumerable<(string Path, int Line, string Text)> AddedLines(PolicyContext context)
    {
        foreach (var change in context.Changes.Changes.Where(c => c.NewContent is not null))
        {
            var before = context.Workspace.TryReadText(change.Path);
            var existing = new HashSet<string>((before ?? "").Replace("\r\n", "\n").Split('\n'));
            var lines = change.NewContent!.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (!existing.Contains(lines[i]))
                    yield return (change.Path, i + 1, lines[i]);
        }
    }
}

/// <summary>Security: blocks credentials or keys from being committed.</summary>
public sealed partial class SecretScanPolicy : IPolicy
{
    public string Id => "SEC-001-secrets";

    public string Category => "security";

    public IEnumerable<PolicyFinding> Evaluate(PolicyContext context) =>
        LineScanner.AddedLines(context).SelectMany(l => Scan(l.Path, l.Line, l.Text));

    public static IEnumerable<PolicyFinding> Scan(string path, int line, string text)
    {
        if (SecretPattern().IsMatch(text))
            yield return new PolicyFinding("SEC-001-secrets", "security", PolicyVerdict.Deny, "Possible hard-coded secret or private key.", path, line);
    }

    [GeneratedRegex("(?i)(AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|sk-[A-Za-z0-9-]{20,}|(password|secret|api[_-]?key)\\s*[:=]\\s*\"[^\"]{8,}\")")]
    private static partial Regex SecretPattern();
}

/// <summary>Compliance: raw client IPs / emails must never be written to logs.</summary>
public sealed partial class PiiLoggingPolicy : IPolicy
{
    public string Id => "CMP-001-pii-logging";

    public string Category => "compliance";

    public IEnumerable<PolicyFinding> Evaluate(PolicyContext context) =>
        LineScanner.AddedLines(context).SelectMany(l => Scan(l.Path, l.Line, l.Text));

    public static IEnumerable<PolicyFinding> Scan(string path, int line, string text)
    {
        if (LogCall().IsMatch(text) && PiiSource().IsMatch(text))
            yield return new PolicyFinding("CMP-001-pii-logging", "compliance", PolicyVerdict.Deny,
                "Log statement includes raw personal data (client IP / email). Hash or omit it.", path, line);
    }

    [GeneratedRegex("\\.Log(Trace|Debug|Information|Warning|Error|Critical)\\(")]
    private static partial Regex LogCall();

    [GeneratedRegex("(RemoteIpAddress|ClientAddress|X-Forwarded-For|\\bEmail\\b)")]
    private static partial Regex PiiSource();
}

/// <summary>Change control: edits to composition root, build files or schema need a human.</summary>
public sealed class ProtectedPathPolicy : IPolicy
{
    private readonly IReadOnlyList<string> _patterns;

    public ProtectedPathPolicy(IReadOnlyList<string> patterns) => _patterns = patterns;

    public string Id => "CHG-001-protected-paths";

    public string Category => "change-control";

    public IEnumerable<PolicyFinding> Evaluate(PolicyContext context)
    {
        foreach (var change in context.Changes.Changes)
        {
            var isNew = !context.Workspace.Exists(change.Path);
            var matched = _patterns.FirstOrDefault(p => Matches(change.Path, p));
            if (matched is not null && !isNew)
                yield return new PolicyFinding(Id, Category, PolicyVerdict.RequireApproval,
                    $"Modifies protected path (rule '{matched}').", change.Path);
            if (change.NewContent is null)
                yield return new PolicyFinding(Id, Category, PolicyVerdict.RequireApproval, "Deletes a file.", change.Path);
        }
    }

    private static bool Matches(string path, string pattern) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? path.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : path.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Change control: large changes are harder to review and must be signed off.</summary>
public sealed class ChangeSizePolicy : IPolicy
{
    private readonly int _maxFiles;
    private readonly int _maxLines;

    public ChangeSizePolicy(int maxFiles, int maxLines)
    {
        _maxFiles = maxFiles;
        _maxLines = maxLines;
    }

    public string Id => "CHG-002-change-size";

    public string Category => "change-control";

    public IEnumerable<PolicyFinding> Evaluate(PolicyContext context)
    {
        var files = context.Changes.Changes.Count;
        var lines = context.Changes.Changes.Sum(c =>
        {
            var (added, removed) = DiffBuilder.Stats(context.Workspace.TryReadText(c.Path), c.NewContent);
            return added + removed;
        });
        if (files > _maxFiles || lines > _maxLines)
            yield return new PolicyFinding(Id, Category, PolicyVerdict.RequireApproval,
                $"Change touches {files} files / {lines} lines (limits {_maxFiles} / {_maxLines}).");
    }
}

/// <summary>Supply chain: only pre-approved packages may be introduced.</summary>
public sealed partial class DependencyAllowListPolicy : IPolicy
{
    private readonly IReadOnlySet<string> _allowed;

    public DependencyAllowListPolicy(IReadOnlySet<string> allowed) => _allowed = allowed;

    public string Id => "SEC-002-dependency-allowlist";

    public string Category => "security";

    public IEnumerable<PolicyFinding> Evaluate(PolicyContext context)
    {
        foreach (var (path, line, text) in LineScanner.AddedLines(context))
        {
            if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = PackageReference().Match(text);
            if (match.Success && !_allowed.Contains(match.Groups[1].Value))
                yield return new PolicyFinding(Id, Category, PolicyVerdict.Deny,
                    $"Package '{match.Groups[1].Value}' is not on the approved dependency list.", path, line);
        }
    }

    [GeneratedRegex("PackageReference\\s+Include=\"([^\"]+)\"")]
    private static partial Regex PackageReference();
}

/// <summary>Autonomy boundary: agents may only write inside approved source/test/doc folders.</summary>
public sealed class ScopePolicy : IPolicy
{
    private readonly IReadOnlyList<string> _prefixes;

    public ScopePolicy(IReadOnlyList<string> prefixes) => _prefixes = prefixes;

    public string Id => "AUT-001-write-scope";

    public string Category => "autonomy";

    public IEnumerable<PolicyFinding> Evaluate(PolicyContext context)
    {
        foreach (var change in context.Changes.Changes)
            if (!_prefixes.Any(p => change.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                yield return new PolicyFinding(Id, Category, PolicyVerdict.Deny,
                    "Write outside the agent's permitted scope.", change.Path);
    }
}
