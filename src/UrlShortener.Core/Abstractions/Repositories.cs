using UrlShortener.Core.Domain;

namespace UrlShortener.Core.Abstractions;

public interface ILinkRepository
{
    /// <summary>Inserts the link. Returns false (without throwing) when the code already exists.</summary>
    Task<bool> TryAddAsync(ShortLink link, CancellationToken ct);

    Task<ShortLink?> GetAsync(string code, CancellationToken ct);

    Task<bool> DeleteAsync(string code, CancellationToken ct);
}

public interface IClickRepository
{
    Task AddAsync(ClickEvent click, CancellationToken ct);

    Task<LinkStats> GetStatsAsync(string code, DateTimeOffset since, int topReferrers, CancellationToken ct);

    Task DeleteForCodeAsync(string code, CancellationToken ct);
}

public interface ICodeGenerator
{
    string Generate(int length);
}
