using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Agentic.Agents.Model;

namespace Agentic.Agents.Tooling;

public interface ITestRunner
{
    Task<TestReport> RunAsync(string workspaceRoot, CancellationToken ct);
}

/// <summary>Runs the real `dotnet test` in the sandbox and parses the TRX results.</summary>
public sealed partial class DotnetTestRunner : ITestRunner
{
    public async Task<TestReport> RunAsync(string workspaceRoot, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var testProjects = Directory.Exists(Path.Combine(workspaceRoot, "tests"))
            ? Directory.GetFiles(Path.Combine(workspaceRoot, "tests"), "*.csproj", SearchOption.AllDirectories)
            : Array.Empty<string>();
        if (testProjects.Length == 0)
            return new TestReport(false, 0, 0, 0, Array.Empty<string>(), new[] { "No test projects found under tests/." }, 0, "dotnet test");

        var resultsDir = Path.Combine(workspaceRoot, "TestResults");
        if (Directory.Exists(resultsDir))
            Directory.Delete(resultsDir, recursive: true);

        var buildErrors = new List<string>();
        var commands = new List<string>();
        foreach (var project in testProjects)
        {
            var relative = Path.GetRelativePath(workspaceRoot, project);
            var args = $"test \"{relative}\" -nologo --logger \"trx\" --results-directory \"{resultsDir}\"";
            commands.Add("dotnet " + args);
            var (exitCode, output) = await RunProcessAsync("dotnet", args, workspaceRoot, ct);
            if (exitCode != 0)
                buildErrors.AddRange(CompileError().Matches(output).Select(m => m.Value.Trim()).Distinct().Take(20));
        }

        var failed = new List<string>();
        int total = 0, passed = 0;
        if (Directory.Exists(resultsDir))
        {
            foreach (var trx in Directory.GetFiles(resultsDir, "*.trx"))
            {
                var doc = XDocument.Load(trx);
                foreach (var result in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
                {
                    total++;
                    var outcome = (string?)result.Attribute("outcome");
                    if (outcome == "Passed")
                        passed++;
                    else if (outcome == "Failed")
                        failed.Add((string?)result.Attribute("testName") ?? "(unknown)");
                }
            }
        }

        var buildSucceeded = buildErrors.Count == 0 && total > 0;
        return new TestReport(buildSucceeded, total, passed, failed.Count, failed, buildErrors,
            Math.Round(stopwatch.Elapsed.TotalSeconds, 1), string.Join(" && ", commands));
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string file, string args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var output = new StringBuilder();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        output.Append(await stdout).Append(await stderr);
        return (process.ExitCode, output.ToString());
    }

    [GeneratedRegex("[^\\s\\\\/]*(\\.cs\\(\\d+,\\d+\\))?: error [A-Z]+\\d+: [^\\[\\r\\n]+")]
    private static partial Regex CompileError();
}
