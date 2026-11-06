using Microsoft.Data.Sqlite;
using UrlShortener.Core.Domain;
using UrlShortener.Infrastructure.Sqlite;

namespace UrlShortener.Tests.Integration;

public sealed class SqliteRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"urlshortener-test-{Guid.NewGuid():N}.db");
    private readonly SqliteDatabase _db;
    private readonly SqliteLinkRepository _links;
    private readonly SqliteClickRepository _clicks;
    private readonly DateTimeOffset _now = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    public SqliteRepositoryTests()
    {
        _db = new SqliteDatabase($"Data Source={_path};Pooling=False");
        _db.EnsureCreated();
        _links = new SqliteLinkRepository(_db);
        _clicks = new SqliteClickRepository(_db);
    }

    [Fact]
    public async Task Links_RoundTripAllFields()
    {
        var link = new ShortLink("Abc1234", "https://example.com", _now, _now.AddDays(7), IsCustomAlias: true);

        Assert.True(await _links.TryAddAsync(link, default));
        Assert.Equal(link, await _links.GetAsync("Abc1234", default));
    }

    [Fact]
    public async Task Links_DuplicateCode_ReturnsFalse_AndCodesAreCaseSensitive()
    {
        await _links.TryAddAsync(new ShortLink("Abc1234", "https://a.example", _now, null, false), default);

        Assert.False(await _links.TryAddAsync(new ShortLink("Abc1234", "https://b.example", _now, null, false), default));
        Assert.True(await _links.TryAddAsync(new ShortLink("abc1234", "https://c.example", _now, null, false), default));
    }

    [Fact]
    public async Task Clicks_AggregateStatsWithinWindow()
    {
        await _clicks.AddAsync(new ClickEvent("c1", _now.AddDays(-40), "https://old.example", null, "h1"), default);
        await _clicks.AddAsync(new ClickEvent("c1", _now.AddDays(-1), "https://a.example", null, "h1"), default);
        await _clicks.AddAsync(new ClickEvent("c1", _now, "https://a.example", null, "h2"), default);
        await _clicks.AddAsync(new ClickEvent("c1", _now, null, null, null), default);
        await _clicks.AddAsync(new ClickEvent("other", _now, "https://a.example", null, "h3"), default);

        var stats = await _clicks.GetStatsAsync("c1", _now.AddDays(-30), 10, default);

        Assert.Equal(4, stats.TotalClicks);
        Assert.Equal(2, stats.UniqueVisitors);
        Assert.Equal(3, stats.Daily.Sum(d => d.Clicks));
        Assert.Equal(new ReferrerCount("https://a.example", 2), Assert.Single(stats.TopReferrers));
        Assert.Equal(_now, stats.LastClickAt);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(file))
                File.Delete(file);
    }
}
