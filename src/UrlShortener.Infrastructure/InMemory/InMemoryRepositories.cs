using System.Collections.Concurrent;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;

namespace UrlShortener.Infrastructure.InMemory;

/// <summary>Thread-safe in-process store. Used for tests and local demos; not durable.</summary>
public sealed class InMemoryLinkRepository : ILinkRepository
{
    private readonly ConcurrentDictionary<string, ShortLink> _links = new(StringComparer.Ordinal);

    public Task<bool> TryAddAsync(ShortLink link, CancellationToken ct) => Task.FromResult(_links.TryAdd(link.Code, link));

    public Task<ShortLink?> GetAsync(string code, CancellationToken ct) =>
        Task.FromResult(_links.TryGetValue(code, out var link) ? link : null);

    public Task<bool> DeleteAsync(string code, CancellationToken ct) => Task.FromResult(_links.TryRemove(code, out _));
}

public sealed class InMemoryClickRepository : IClickRepository
{
    private readonly ConcurrentQueue<ClickEvent> _clicks = new();

    public Task AddAsync(ClickEvent click, CancellationToken ct)
    {
        _clicks.Enqueue(click);
        return Task.CompletedTask;
    }

    public Task<LinkStats> GetStatsAsync(string code, DateTimeOffset since, int topReferrers, CancellationToken ct)
    {
        var all = _clicks.Where(c => c.Code == code).ToList();
        var window = all.Where(c => c.OccurredAt >= since).ToList();

        var daily = window
            .GroupBy(c => DateOnly.FromDateTime(c.OccurredAt.UtcDateTime))
            .OrderBy(g => g.Key)
            .Select(g => new DailyClicks(g.Key, g.LongCount()))
            .ToList();

        var referrers = window
            .Where(c => !string.IsNullOrEmpty(c.Referrer))
            .GroupBy(c => c.Referrer!)
            .Select(g => new ReferrerCount(g.Key, g.LongCount()))
            .OrderByDescending(r => r.Clicks).ThenBy(r => r.Referrer, StringComparer.Ordinal)
            .Take(topReferrers)
            .ToList();

        var stats = new LinkStats(
            code,
            all.Count,
            all.Where(c => c.ClientHash is not null).Select(c => c.ClientHash).Distinct().LongCount(),
            all.Count == 0 ? null : all.Max(c => c.OccurredAt),
            daily,
            referrers);
        return Task.FromResult(stats);
    }

    public Task DeleteForCodeAsync(string code, CancellationToken ct)
    {
        // ConcurrentQueue has no removal; rebuild atomically enough for an in-memory store.
        var keep = _clicks.Where(c => c.Code != code).ToList();
        _clicks.Clear();
        foreach (var c in keep)
            _clicks.Enqueue(c);
        return Task.CompletedTask;
    }
}
