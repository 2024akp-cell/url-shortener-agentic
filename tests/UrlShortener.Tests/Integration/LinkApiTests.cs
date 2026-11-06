using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.Core.Domain;
using UrlShortener.Tests.TestSupport;

namespace UrlShortener.Tests.Integration;

public class LinkApiTests : IClassFixture<ShortenerApiFactory>
{
    private readonly ShortenerApiFactory _factory;
    private readonly HttpClient _client;

    public LinkApiTests(ShortenerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNoRedirectClient();
    }

    private async Task<LinkResponse> CreateLinkAsync(string url, string? alias = null, DateTimeOffset? expiresAt = null)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest(url, alias, expiresAt));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LinkResponse>())!;
    }

    [Fact]
    public async Task CreateLink_Returns201WithLocationAndShortUrl()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/page", null, null));
        var body = await response.Content.ReadFromJsonAsync<LinkResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/links/{body!.Code}", response.Headers.Location!.OriginalString);
        Assert.Equal($"https://sho.rt/{body.Code}", body.ShortUrl);
    }

    [Fact]
    public async Task CreateLink_InvalidUrl_Returns400ValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("ftp://example.com", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task CreateLink_DuplicateAlias_Returns409()
    {
        await CreateLinkAsync("https://example.com/a", "dup-alias");

        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/b", "dup-alias", null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Redirect_ActiveLink_Returns302ToTargetWithNoStore()
    {
        var link = await CreateLinkAsync("https://example.com/target");

        var response = await _client.GetAsync($"/{link.Code}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://example.com/target", response.Headers.Location!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Redirect_UnknownCode_Returns404()
    {
        var response = await _client.GetAsync("/doesNotExist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Redirect_ExpiredLink_Returns410()
    {
        var link = await CreateLinkAsync("https://example.com/soon-gone", expiresAt: _factory.Clock.GetUtcNow().AddMinutes(5));
        _factory.Clock.Advance(TimeSpan.FromMinutes(10));

        var response = await _client.GetAsync($"/{link.Code}");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Stats_CountsClicksAndReferrers()
    {
        var link = await CreateLinkAsync("https://example.com/tracked");
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/{link.Code}");
            request.Headers.Referrer = new Uri("https://news.example/");
            await _client.SendAsync(request);
        }

        var stats = await _client.GetFromJsonAsync<LinkStats>($"/api/links/{link.Code}/stats");

        Assert.Equal(3, stats!.TotalClicks);
        Assert.Equal(3, stats.Daily.Sum(d => d.Clicks));
        Assert.Equal("https://news.example/", Assert.Single(stats.TopReferrers).Referrer);
    }

    [Fact]
    public async Task GetLink_ReturnsDetails_AndDeleteRemovesIt()
    {
        var link = await CreateLinkAsync("https://example.com/delete-me", "delete-me");

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/links/delete-me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/links/{link.Code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/links/delete-me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/delete-me")).StatusCode);
    }

    [Fact]
    public async Task Responses_CarryCorrelationId()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-ID", "trace-123");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("trace-123", response.Headers.GetValues("X-Correlation-ID").Single());
    }
}
