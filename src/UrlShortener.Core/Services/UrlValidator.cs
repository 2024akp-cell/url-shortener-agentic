using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace UrlShortener.Core.Services;

/// <summary>
/// Validates target URLs and custom aliases. Rejecting non-http(s) schemes and
/// loopback/private addresses stops the service being used for javascript: payloads
/// or as a pivot into internal networks.
/// </summary>
public sealed partial class UrlValidator
{
    private static readonly HashSet<string> ReservedAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "health", "openapi", "swagger", "admin", "static",
    };

    private readonly ShortenerOptions _options;

    public UrlValidator(ShortenerOptions options) => _options = options;

    public string? ValidateTargetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "URL is required.";
        if (url.Length > _options.MaxUrlLength)
            return $"URL must be at most {_options.MaxUrlLength} characters.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "URL must be absolute.";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "Only http and https URLs are allowed.";
        if (string.IsNullOrEmpty(uri.Host))
            return "URL must include a host.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "URLs with embedded credentials are not allowed.";
        if (IsLocalOrPrivate(uri))
            return "URLs targeting local or private network addresses are not allowed.";
        if (_options.BlockedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
            return "This host cannot be shortened.";
        return null;
    }

    public string? ValidateAlias(string alias)
    {
        if (!AliasPattern().IsMatch(alias))
            return "Alias must be 3-32 characters: letters, digits, '-' or '_'.";
        if (ReservedAliases.Contains(alias))
            return "Alias is reserved.";
        return null;
    }

    private static bool IsLocalOrPrivate(Uri uri)
    {
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(uri.DnsSafeHost, out var ip))
            return false;

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal
                   || (ip.IsIPv4MappedToIPv6 && IsPrivateV4(ip.MapToIPv4()));

        return IsPrivateV4(ip);
    }

    private static bool IsPrivateV4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || b[0] == 127
               || b[0] == 0
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{3,32}$")]
    private static partial Regex AliasPattern();
}
