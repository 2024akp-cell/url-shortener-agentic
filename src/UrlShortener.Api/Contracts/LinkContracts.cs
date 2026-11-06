using UrlShortener.Core.Domain;

namespace UrlShortener.Api.Contracts;

public sealed record CreateLinkRequest(string? Url, string? CustomAlias, DateTimeOffset? ExpiresAt);

public sealed record LinkResponse(
    string Code,
    string ShortUrl,
    string TargetUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsCustomAlias)
{
    public static LinkResponse From(ShortLink link, string baseUrl) => new(
        link.Code,
        $"{baseUrl.TrimEnd('/')}/{link.Code}",
        link.TargetUrl,
        link.CreatedAt,
        link.ExpiresAt,
        link.IsCustomAlias);
}
