using UrlShortener.Core.Services;

namespace UrlShortener.Api.Endpoints;

/// <summary>Single place that maps domain outcomes to HTTP semantics (RFC 9457 problem details).</summary>
internal static class ResultMapping
{
    public static IResult ToProblem<T>(this ServiceResult<T> result) => result.Error switch
    {
        LinkError.InvalidUrl => Validation("url", result.Message),
        LinkError.InvalidAlias => Validation("customAlias", result.Message),
        LinkError.InvalidExpiry => Validation("expiresAt", result.Message),
        LinkError.AliasTaken => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
        LinkError.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: result.Message),
        LinkError.Expired => Results.Problem(statusCode: StatusCodes.Status410Gone, title: result.Message),
        LinkError.CodeSpaceExhausted => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: result.Message),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected error."),
    };

    private static IResult Validation(string field, string? message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = new[] { message ?? "Invalid value." } });
}
