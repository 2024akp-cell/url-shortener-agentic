using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Infrastructure;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>When set, management endpoints require a matching X-Api-Key header. Empty = open (dev only).</summary>
    public string? ApiKey { get; set; }
}

public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly IOptionsMonitor<SecurityOptions> _options;

    public ApiKeyFilter(IOptionsMonitor<SecurityOptions> options) => _options = options;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = _options.CurrentValue.ApiKey;
        if (string.IsNullOrEmpty(expected))
            return await next(context);

        var supplied = context.HttpContext.Request.Headers[HeaderName].ToString();
        var match = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
        return match
            ? await next(context)
            : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "A valid API key is required.");
    }
}
