namespace UrlShortener.Core.Services;

public sealed class ShortenerOptions
{
    public const string SectionName = "Shortener";

    /// <summary>Public base URL used to build short links, e.g. https://sho.rt</summary>
    public string BaseUrl { get; set; } = "http://localhost:5080";

    public int CodeLength { get; set; } = 7;

    /// <summary>Bounded retries when a generated code collides with an existing one.</summary>
    public int MaxCodeGenerationAttempts { get; set; } = 5;

    public int MaxUrlLength { get; set; } = 2048;

    public int MaxExpiryDays { get; set; } = 365;

    /// <summary>Salt for hashing client addresses in analytics. Must be overridden in production.</summary>
    public string AnalyticsSalt { get; set; } = "dev-only-salt";

    /// <summary>Hosts that may never be shortened (e.g. our own domain, to prevent redirect loops).</summary>
    public List<string> BlockedHosts { get; set; } = new();
}
