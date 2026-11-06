using System.Text;
using System.Text.RegularExpressions;
using Agentic.Agents.Model;
using Agentic.Agents.Tooling;
using Agentic.Orchestration.Agents;
using Agentic.Orchestration.Context;

namespace Agentic.Agents.Stages;

/// <summary>
/// Brownfield reasoning: builds a structural model of the existing codebase (projects, layers,
/// types, HTTP surface, tests and project dependencies) that planning uses for impact analysis.
/// </summary>
public sealed partial class CodebaseAnalysisAgent : IAgent
{
    public string Id => "codebase-analysis";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var ws = ctx.Workspace;
        var projects = ws.ListFiles(".csproj");
        if (projects.Count == 0)
            return Task.FromResult(AgentResult.Failed("No projects found in workspace; brownfield analysis requires an existing codebase."));

        var references = projects.ToDictionary(
            p => ProjectName(p),
            p => (IReadOnlyList<string>)ProjectReference().Matches(ws.ReadText(p)).Select(m => ProjectName(m.Groups[1].Value.Replace('\\', '/'))).ToList());

        var files = new List<CodeFile>();
        var endpoints = new List<Endpoint>();
        var tests = new List<TestCase>();
        foreach (var path in ws.ListFiles(".cs"))
        {
            ct.ThrowIfCancellationRequested();
            var text = ws.ReadText(path);
            var project = projects.Where(p => path.StartsWith(p[..(p.LastIndexOf('/') + 1)], StringComparison.Ordinal))
                .Select(ProjectName).FirstOrDefault() ?? "(none)";
            var types = TypeDeclaration().Matches(text).Select(m => m.Groups[2].Value).Distinct().ToList();
            files.Add(new CodeFile(path, project, Layer(project), types, text.Count(c => c == '\n') + 1));

            endpoints.AddRange(RouteScanner.Scan(text).Select(r => new Endpoint(r.Method, r.Route, path)));
            if (text.Contains("[Fact]", StringComparison.Ordinal) || text.Contains("[Theory]", StringComparison.Ordinal))
            {
                var cls = types.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
                tests.AddRange(TestMethod().Matches(text).Select(m => new TestCase(cls, m.Groups[1].Value, path)));
            }
        }

        var model = new CodebaseModel(projects, files, endpoints, tests, references);
        ctx.Log($"Indexed {files.Count} files, {endpoints.Count} endpoints, {tests.Count} tests across {projects.Count} projects");
        return Task.FromResult(new AgentResult
        {
            Summary = $"{projects.Count} projects, {files.Count} files, {endpoints.Count} endpoints, {tests.Count} tests",
            Outputs = { [Keys.CodebaseModel] = model, [Keys.CodebaseDoc] = Render(model) },
            Decisions =
            {
                new DecisionDraft(DecisionKind.Interpretation,
                    "Layered architecture detected: " + string.Join(" → ", references.OrderBy(r => r.Value.Count).Select(r => r.Key)),
                    "Derived from ProjectReference edges; used to constrain where changes may land."),
            },
        });
    }

    private static string ProjectName(string path) => Path.GetFileNameWithoutExtension(path);

    private static string Layer(string project) => project switch
    {
        _ when project.EndsWith(".Core", StringComparison.Ordinal) => "Domain",
        _ when project.EndsWith(".Infrastructure", StringComparison.Ordinal) => "Persistence",
        _ when project.EndsWith(".Api", StringComparison.Ordinal) => "API",
        _ when project.Contains("Test", StringComparison.Ordinal) => "Tests",
        _ => "Other",
    };

    private static string Render(CodebaseModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 2. Codebase analysis (brownfield)");
        sb.AppendLine();
        sb.AppendLine("### Module dependency graph");
        sb.AppendLine("```mermaid");
        sb.AppendLine("flowchart LR");
        foreach (var (project, refs) in model.ProjectReferences)
            foreach (var r in refs)
                sb.AppendLine($"    {project.Replace(".", "_")} --> {r.Replace(".", "_")}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Layers");
        sb.AppendLine("| Layer | Files | Key types |");
        sb.AppendLine("|---|---|---|");
        foreach (var layer in model.Files.GroupBy(f => f.Layer).OrderBy(g => g.Key))
            sb.AppendLine($"| {layer.Key} | {layer.Count()} | {string.Join(", ", layer.SelectMany(f => f.Types).Take(8))} |");
        sb.AppendLine();
        sb.AppendLine("### HTTP surface");
        sb.AppendLine("| Method | Route | Defined in |");
        sb.AppendLine("|---|---|---|");
        foreach (var e in model.Endpoints)
            sb.AppendLine($"| {e.Method} | `{e.Route}` | `{e.File}` |");
        sb.AppendLine();
        sb.AppendLine($"### Test inventory: {model.Tests.Count} tests in {model.Tests.Select(t => t.File).Distinct().Count()} files");
        return sb.ToString();
    }

    [GeneratedRegex("ProjectReference\\s+Include=\"([^\"]+)\"")]
    private static partial Regex ProjectReference();

    [GeneratedRegex("\\b(class|record|interface|enum)\\s+([A-Z][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex("\\[(?:Fact|Theory)\\][\\s\\S]*?public\\s+(?:async\\s+)?[\\w<>]+\\s+(\\w+)\\s*\\(")]
    private static partial Regex TestMethod();
}
