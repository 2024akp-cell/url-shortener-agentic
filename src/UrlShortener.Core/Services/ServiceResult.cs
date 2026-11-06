namespace UrlShortener.Core.Services;

public enum LinkError
{
    None,
    InvalidUrl,
    InvalidAlias,
    InvalidExpiry,
    AliasTaken,
    NotFound,
    Expired,
    CodeSpaceExhausted,
}

/// <summary>Expected business outcomes are returned, not thrown; exceptions are reserved for faults.</summary>
public sealed record ServiceResult<T>(T? Value, LinkError Error, string? Message)
{
    public bool IsSuccess => Error == LinkError.None;

    public static ServiceResult<T> Ok(T value) => new(value, LinkError.None, null);

    public static ServiceResult<T> Fail(LinkError error, string message) => new(default, error, message);
}
