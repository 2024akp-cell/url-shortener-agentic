using System.Text.RegularExpressions;

namespace Agentic.Agents.Tooling;

/// <summary>Extracts minimal-API routes from source, resolving MapGroup prefixes.</summary>
public static partial class RouteScanner
{
    public static IEnumerable<(string Method, string Route)> Scan(string source)
    {
        var prefix = Group().Match(source) is { Success: true } g ? g.Groups[1].Value.TrimEnd('/') : "";
        foreach (Match m in Verb().Matches(source))
        {
            var route = Constraint().Replace(m.Groups[2].Value.Replace("{{", "{").Replace("}}", "}"), "{$1}");
            var full = prefix.Length == 0 ? route : prefix + (route.Length == 0 ? "" : "/" + route.TrimStart('/'));
            yield return (m.Groups[1].Value.ToUpperInvariant(), full.Length == 0 ? "/" : full);
        }
    }

    [GeneratedRegex("MapGroup\\(\"([^\"]*)\"\\)")]
    private static partial Regex Group();

    [GeneratedRegex("\\.Map(Get|Post|Put|Delete|Patch)\\(\"([^\"]*)\"")]
    private static partial Regex Verb();

    [GeneratedRegex("\\{(\\w+):regex\\(.*?\\)\\}")]
    private static partial Regex Constraint();
}
