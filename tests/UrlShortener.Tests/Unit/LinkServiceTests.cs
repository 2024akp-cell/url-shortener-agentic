using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;
using UrlShortener.Core.Services;
using UrlShortener.Infrastructure.InMemory;
using UrlShortener.Tests.TestSupport;

namespace UrlShortener.Tests.Unit;

public class LinkServiceTests
{
    private static readonly ClickContext AnyClick = new("https://news.example", "test-agent", "203.0.113.7");

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryLinkRepository _links = new();
    private readonly InMemoryClickRepository _clicks = new();
    private readonly ShortenerOptions _options = new();

    private LinkService CreateService(ICodeGenerator? codes = null) =>
        new(_links, _clicks, codes ?? new Base62CodeGenerator(), new UrlValidator(_options), _options, _clock);

    [Fact]
    public async Task CreateAsync_ValidUrl_ReturnsLinkWithGeneratedCode()
    {
        var result = await CreateService().CreateAsync(new CreateLinkCommand("https://example.com", null, null), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(_options.CodeLength, result.Value!.Code.Length);
        Assert.False(result.Value.IsCustomAlias);
        Assert.Equal(_clock.GetUtcNow(), result.Value.CreatedAt);
    }

    [Fact]
    public async Task CreateAsync_InvalidUrl_ReturnsInvalidUrl()
    {
        var result = await CreateService().CreateAsync(new CreateLinkCommand("javascript:alert(1)", null, null), default);

        Assert.Equal(LinkError.InvalidUrl, result.Error);
    }

    [Fact]
    public async Task CreateAsync_DuplicateAlias_ReturnsAliasTaken()
    {
        var service = CreateService();
        await service.CreateAsync(new CreateLinkCommand("https://a.example", "promo", null), default);

        var second = await service.CreateAsync(new CreateLinkCommand("https://b.example", "promo", null), default);

        Assert.Equal(LinkError.AliasTaken, second.Error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(400)]
    public async Task CreateAsync_ExpiryOutsideAllowedWindow_ReturnsInvalidExpiry(int daysFromNow)
    {
        var expiry = _clock.GetUtcNow().AddDays(daysFromNow);

        var result = await CreateService().CreateAsync(new CreateLinkCommand("https://example.com", null, expiry), default);

        Assert.Equal(LinkError.InvalidExpiry, result.Error);
    }

    [Fact]
    public async Task CreateAsync_CodeCollision_RetriesWithNewCode()
    {
        await _links.TryAddAsync(new ShortLink("taken01", "https://x.example", _clock.GetUtcNow(), null, false), default);
        var codes = new SequenceCodeGenerator("taken01", "fresh01");

        var result = await CreateService(codes).CreateAsync(new CreateLinkCommand("https://example.com", null, null), default);

        Assert.Equal("fresh01", result.Value!.Code);
        Assert.Equal(2, codes.Calls);
    }

    [Fact]
    public async Task CreateAsync_PersistentCollisions_FailAfterBoundedAttempts()
    {
        await _links.TryAddAsync(new ShortLink("taken01", "https://x.example", _clock.GetUtcNow(), null, false), default);
        var codes = new SequenceCodeGenerator("taken01");

        var result = await CreateService(codes).CreateAsync(new CreateLinkCommand("https://example.com", null, null), default);

        Assert.Equal(LinkError.CodeSpaceExhausted, result.Error);
        Assert.Equal(_options.MaxCodeGenerationAttempts, codes.Calls);
    }

    [Fact]
    public async Task ResolveAsync_ActiveLink_ReturnsLinkAndRecordsClick()
    {
        var service = CreateService();
        var link = (await service.CreateAsync(new CreateLinkCommand("https://example.com", null, null), default)).Value!;

        var resolved = await service.ResolveAsync(link.Code, AnyClick, default);
        var stats = await service.GetStatsAsync(link.Code, 30, default);

        Assert.True(resolved.IsSuccess);
        Assert.Equal(1, stats.Value!.TotalClicks);
    }

    [Fact]
    public async Task ResolveAsync_ExpiredLink_ReturnsExpiredAndRecordsNoClick()
    {
        var service = CreateService();
        var link = (await service.CreateAsync(
            new CreateLinkCommand("https://example.com", null, _clock.GetUtcNow().AddHours(1)), default)).Value!;
        _clock.Advance(TimeSpan.FromHours(2));

        var resolved = await service.ResolveAsync(link.Code, AnyClick, default);
        var stats = await service.GetStatsAsync(link.Code, 30, default);

        Assert.Equal(LinkError.Expired, resolved.Error);
        Assert.Equal(0, stats.Value!.TotalClicks);
    }

    [Fact]
    public async Task ResolveAsync_UnknownCode_ReturnsNotFound()
    {
        var result = await CreateService().ResolveAsync("nope123", AnyClick, default);

        Assert.Equal(LinkError.NotFound, result.Error);
    }

    [Fact]
    public async Task ResolveAsync_StoresHashedClientAddressNeverRaw()
    {
        var service = CreateService();
        var link = (await service.CreateAsync(new CreateLinkCommand("https://example.com", null, null), default)).Value!;

        await service.ResolveAsync(link.Code, AnyClick, default);
        await service.ResolveAsync(link.Code, AnyClick with { ClientAddress = "198.51.100.1" }, default);
        var stats = (await service.GetStatsAsync(link.Code, 30, default)).Value!;

        Assert.Equal(2, stats.UniqueVisitors);
        Assert.DoesNotContain("203.0.113.7", service.HashClient("203.0.113.7")!);
    }

    [Fact]
    public async Task DeleteAsync_RemovesLinkAndAnalytics()
    {
        var service = CreateService();
        var link = (await service.CreateAsync(new CreateLinkCommand("https://example.com", null, null), default)).Value!;
        await service.ResolveAsync(link.Code, AnyClick, default);

        Assert.True(await service.DeleteAsync(link.Code, default));
        Assert.Equal(LinkError.NotFound, (await service.GetAsync(link.Code, default)).Error);
        Assert.False(await service.DeleteAsync(link.Code, default));
    }
}
