using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace UrlShortener.Tests.TestSupport;

/// <summary>Boots the real HTTP pipeline against in-memory storage and a controllable clock.</summary>
public sealed class ShortenerApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    public ShortenerApiFactory()
        : this(null)
    {
    }

    // Internal: xUnit class fixtures must expose exactly one public constructor.
    internal ShortenerApiFactory(Dictionary<string, string?>? settings)
    {
        _settings = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "InMemory",
            ["Shortener:BaseUrl"] = "https://sho.rt",
            ["RateLimiting:PermitLimit"] = "1000",
        };
        foreach (var (key, value) in settings ?? new())
            _settings[key] = value;
    }

    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
