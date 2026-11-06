namespace UrlShortener.Core.Domain;

/// <summary>A short code mapped to a target URL. Immutable once created.</summary>
public sealed record ShortLink(
    string Code,
    string TargetUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsCustomAlias)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && expiry <= now;
}
