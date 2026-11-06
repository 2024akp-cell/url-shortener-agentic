using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.Tests.TestSupport;

namespace UrlShortener.Tests.Integration;

public class SecurityAndLimitsTests
{
    [Fact]
    public async Task CreateLink_OverRateLimit_Returns429WithRetryAfter()
    {
        await using var factory = new ShortenerApiFactory(new() { ["RateLimiting:PermitLimit"] = "2" });
        var client = factory.CreateClient();
        var request = new CreateLinkRequest("https://example.com", null, null);

        await client.PostAsJsonAsync("/api/links", request);
        await client.PostAsJsonAsync("/api/links", request);
        var third = await client.PostAsJsonAsync("/api/links", request);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task ManagementEndpoints_WithApiKeyConfigured_RejectMissingOrWrongKey()
    {
        await using var factory = new ShortenerApiFactory(new() { ["Security:ApiKey"] = "s3cret-key" });
        var client = factory.CreateClient();
        var request = new CreateLinkRequest("https://example.com", null, null);

        var missing = await client.PostAsJsonAsync("/api/links", request);
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        var wrong = await client.PostAsJsonAsync("/api/links", request);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", "s3cret-key");
        var correct = await client.PostAsJsonAsync("/api/links", request);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Created, correct.StatusCode);
    }

    [Fact]
    public async Task Redirect_IsPublicEvenWhenApiKeyConfigured()
    {
        await using var factory = new ShortenerApiFactory(new() { ["Security:ApiKey"] = "s3cret-key" });
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", "s3cret-key");
        var link = await (await admin.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com", "public-one", null)))
            .Content.ReadFromJsonAsync<LinkResponse>();

        var response = await factory.CreateNoRedirectClient().GetAsync($"/{link!.Code}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
