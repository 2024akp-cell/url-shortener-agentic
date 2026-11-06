using UrlShortener.Core.Domain;
using UrlShortener.Core.Services;

namespace UrlShortener.Api.Endpoints;

public static class RedirectEndpoints
{
    public static IEndpointRouteBuilder MapRedirectEndpoints(this IEndpointRouteBuilder app)
    {
        // Literal routes (/health, /api/...) take precedence over this parameterised route.
        app.MapGet("/{code:regex(^[A-Za-z0-9_-]{{3,32}}$)}", RedirectAsync)
            .WithTags("Redirect")
            .WithName("RedirectToTarget")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);
        return app;
    }

    private static async Task<IResult> RedirectAsync(
        string code, HttpContext http, LinkService links, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger("UrlShortener.Redirect");
        var click = new ClickContext(
            NullIfEmpty(http.Request.Headers.Referer.ToString()),
            NullIfEmpty(http.Request.Headers.UserAgent.ToString()),
            http.Connection.RemoteIpAddress?.ToString());
        logger.LogDebug("Resolving short code {Code}", code);

        var result = await links.ResolveAsync(code, click, ct);
        if (!result.IsSuccess)
            return result.ToProblem();

        // 302 + no-store so every visit reaches us and is counted (301s are cached by browsers).
        http.Response.Headers.CacheControl = "no-store";
        return Results.Redirect(result.Value!.TargetUrl, permanent: false);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
