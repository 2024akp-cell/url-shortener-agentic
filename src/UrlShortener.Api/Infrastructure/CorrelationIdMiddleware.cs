namespace UrlShortener.Api.Infrastructure;

/// <summary>Propagates (or creates) X-Correlation-ID and adds it to the logging scope.</summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsValid(incoming) ? incoming : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            await _next(context);
    }

    // Reject oversized or non-printable values so the header cannot be used for log injection.
    private static bool IsValid(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}
