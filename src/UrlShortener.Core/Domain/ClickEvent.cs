namespace UrlShortener.Core.Domain;

/// <summary>
/// A single redirect. The client address is stored only as a salted hash (never raw)
/// so analytics can count unique visitors without retaining personal data.
/// </summary>
public sealed record ClickEvent(
    string Code,
    DateTimeOffset OccurredAt,
    string? Referrer,
    string? UserAgent,
    string? ClientHash);

/// <summary>Request metadata captured at redirect time.</summary>
public sealed record ClickContext(string? Referrer, string? UserAgent, string? ClientAddress);

public sealed record DailyClicks(DateOnly Day, long Clicks);

public sealed record ReferrerCount(string Referrer, long Clicks);

public sealed record LinkStats(
    string Code,
    long TotalClicks,
    long UniqueVisitors,
    DateTimeOffset? LastClickAt,
    IReadOnlyList<DailyClicks> Daily,
    IReadOnlyList<ReferrerCount> TopReferrers);
