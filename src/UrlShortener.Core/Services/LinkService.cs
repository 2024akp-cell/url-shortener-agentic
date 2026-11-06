using System.Security.Cryptography;
using System.Text;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;

namespace UrlShortener.Core.Services;

public sealed record CreateLinkCommand(string? Url, string? CustomAlias, DateTimeOffset? ExpiresAt);

/// <summary>Application service holding all link business rules. Transport-agnostic.</summary>
public sealed class LinkService
{
    private const int MaxHeaderLength = 512;

    private readonly ILinkRepository _links;
    private readonly IClickRepository _clicks;
    private readonly ICodeGenerator _codes;
    private readonly UrlValidator _validator;
    private readonly ShortenerOptions _options;
    private readonly TimeProvider _time;

    public LinkService(
        ILinkRepository links,
        IClickRepository clicks,
        ICodeGenerator codes,
        UrlValidator validator,
        ShortenerOptions options,
        TimeProvider time)
    {
        _links = links;
        _clicks = clicks;
        _codes = codes;
        _validator = validator;
        _options = options;
        _time = time;
    }

    public async Task<ServiceResult<ShortLink>> CreateAsync(CreateLinkCommand command, CancellationToken ct)
    {
        var urlError = _validator.ValidateTargetUrl(command.Url);
        if (urlError is not null)
            return ServiceResult<ShortLink>.Fail(LinkError.InvalidUrl, urlError);

        var now = _time.GetUtcNow();
        if (command.ExpiresAt is { } expiresAt)
        {
            if (expiresAt <= now)
                return ServiceResult<ShortLink>.Fail(LinkError.InvalidExpiry, "Expiry must be in the future.");
            if (expiresAt > now.AddDays(_options.MaxExpiryDays))
                return ServiceResult<ShortLink>.Fail(LinkError.InvalidExpiry, $"Expiry must be within {_options.MaxExpiryDays} days.");
        }

        if (!string.IsNullOrEmpty(command.CustomAlias))
        {
            var aliasError = _validator.ValidateAlias(command.CustomAlias);
            if (aliasError is not null)
                return ServiceResult<ShortLink>.Fail(LinkError.InvalidAlias, aliasError);

            var aliased = new ShortLink(command.CustomAlias, command.Url!, now, command.ExpiresAt, IsCustomAlias: true);
            return await _links.TryAddAsync(aliased, ct)
                ? ServiceResult<ShortLink>.Ok(aliased)
                : ServiceResult<ShortLink>.Fail(LinkError.AliasTaken, "Alias is already in use.");
        }

        // Collisions are astronomically rare at 62^7, but retries are bounded so a
        // saturated keyspace or a broken generator fails fast instead of looping.
        for (var attempt = 0; attempt < _options.MaxCodeGenerationAttempts; attempt++)
        {
            var link = new ShortLink(_codes.Generate(_options.CodeLength), command.Url!, now, command.ExpiresAt, IsCustomAlias: false);
            if (await _links.TryAddAsync(link, ct))
                return ServiceResult<ShortLink>.Ok(link);
        }

        return ServiceResult<ShortLink>.Fail(LinkError.CodeSpaceExhausted, "Could not allocate a unique code. Try again.");
    }

    public async Task<ServiceResult<ShortLink>> GetAsync(string code, CancellationToken ct)
    {
        var link = await _links.GetAsync(code, ct);
        return link is null
            ? ServiceResult<ShortLink>.Fail(LinkError.NotFound, "Short link not found.")
            : ServiceResult<ShortLink>.Ok(link);
    }

    /// <summary>Resolves a code for redirect and records the click.</summary>
    public async Task<ServiceResult<ShortLink>> ResolveAsync(string code, ClickContext click, CancellationToken ct)
    {
        var link = await _links.GetAsync(code, ct);
        if (link is null)
            return ServiceResult<ShortLink>.Fail(LinkError.NotFound, "Short link not found.");

        var now = _time.GetUtcNow();
        if (link.IsExpired(now))
            return ServiceResult<ShortLink>.Fail(LinkError.Expired, "Short link has expired.");

        await _clicks.AddAsync(
            new ClickEvent(link.Code, now, Truncate(click.Referrer), Truncate(click.UserAgent), HashClient(click.ClientAddress)),
            ct);
        return ServiceResult<ShortLink>.Ok(link);
    }

    public async Task<ServiceResult<LinkStats>> GetStatsAsync(string code, int days, CancellationToken ct)
    {
        var link = await _links.GetAsync(code, ct);
        if (link is null)
            return ServiceResult<LinkStats>.Fail(LinkError.NotFound, "Short link not found.");

        var window = Math.Clamp(days, 1, 365);
        var since = _time.GetUtcNow().AddDays(-window);
        return ServiceResult<LinkStats>.Ok(await _clicks.GetStatsAsync(code, since, topReferrers: 10, ct));
    }

    public async Task<bool> DeleteAsync(string code, CancellationToken ct)
    {
        if (!await _links.DeleteAsync(code, ct))
            return false;
        await _clicks.DeleteForCodeAsync(code, ct);
        return true;
    }

    internal string? HashClient(string? address)
    {
        if (string.IsNullOrEmpty(address))
            return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(_options.AnalyticsSalt + "|" + address));
        return Convert.ToHexString(bytes, 0, 16);
    }

    private static string? Truncate(string? value) =>
        value is { Length: > MaxHeaderLength } ? value[..MaxHeaderLength] : value;
}
