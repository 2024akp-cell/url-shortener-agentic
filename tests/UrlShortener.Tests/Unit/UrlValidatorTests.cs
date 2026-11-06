using UrlShortener.Core.Services;

namespace UrlShortener.Tests.Unit;

public class UrlValidatorTests
{
    private readonly UrlValidator _validator = new(new ShortenerOptions { BlockedHosts = { "sho.rt" } });

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1#frag")]
    [InlineData("https://sub.example.co.uk:8443/a/b")]
    [InlineData("https://8.8.8.8/dns")]
    public void ValidateTargetUrl_AcceptsPublicHttpUrls(string url) =>
        Assert.Null(_validator.ValidateTargetUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("data:text/html,hi")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("http://localhost/admin")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://10.0.0.5")]
    [InlineData("http://192.168.1.1")]
    [InlineData("http://172.20.0.1")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/")]
    [InlineData("https://sho.rt/abc")]
    public void ValidateTargetUrl_RejectsUnsafeOrInvalidUrls(string? url) =>
        Assert.NotNull(_validator.ValidateTargetUrl(url));

    [Fact]
    public void ValidateTargetUrl_RejectsOverlongUrls() =>
        Assert.NotNull(_validator.ValidateTargetUrl("https://example.com/" + new string('a', 2048)));

    [Theory]
    [InlineData("my-link")]
    [InlineData("Promo_2026")]
    [InlineData("abc")]
    public void ValidateAlias_AcceptsWellFormedAliases(string alias) =>
        Assert.Null(_validator.ValidateAlias(alias));

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("slash/alias")]
    [InlineData("api")]
    [InlineData("HEALTH")]
    [InlineData("this-alias-is-way-too-long-to-be-accepted")]
    public void ValidateAlias_RejectsInvalidOrReservedAliases(string alias) =>
        Assert.NotNull(_validator.ValidateAlias(alias));
}
