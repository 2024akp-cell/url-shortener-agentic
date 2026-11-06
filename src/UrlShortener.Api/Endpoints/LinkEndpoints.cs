using UrlShortener.Api.Contracts;
using UrlShortener.Api.Infrastructure;
using UrlShortener.Core.Domain;
using UrlShortener.Core.Services;

namespace UrlShortener.Api.Endpoints;

public static class LinkEndpoints
{
    public static IEndpointRouteBuilder MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/links")
            .WithTags("Links")
            .AddEndpointFilter<ApiKeyFilter>();

        group.MapPost("", CreateAsync)
            .RequireRateLimiting(ServiceRegistration.CreateRateLimitPolicy)
            .WithName("CreateLink")
            .Produces<LinkResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/{code}", GetAsync)
            .WithName("GetLink")
            .Produces<LinkResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{code}/stats", GetStatsAsync)
            .WithName("GetLinkStats")
            .Produces<LinkStats>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{code}", DeleteAsync)
            .WithName("DeleteLink")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateLinkRequest request, LinkService links, ShortenerOptions options, CancellationToken ct)
    {
        var result = await links.CreateAsync(new CreateLinkCommand(request.Url, request.CustomAlias, request.ExpiresAt), ct);
        if (!result.IsSuccess)
            return result.ToProblem();

        var response = LinkResponse.From(result.Value!, options.BaseUrl);
        return Results.Created($"/api/links/{response.Code}", response);
    }

    private static async Task<IResult> GetAsync(string code, LinkService links, ShortenerOptions options, CancellationToken ct)
    {
        var result = await links.GetAsync(code, ct);
        return result.IsSuccess ? Results.Ok(LinkResponse.From(result.Value!, options.BaseUrl)) : result.ToProblem();
    }

    private static async Task<IResult> GetStatsAsync(string code, LinkService links, CancellationToken ct, int days = 30)
    {
        var result = await links.GetStatsAsync(code, days, ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> DeleteAsync(string code, LinkService links, CancellationToken ct) =>
        await links.DeleteAsync(code, ct)
            ? Results.NoContent()
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Short link not found.");
}
