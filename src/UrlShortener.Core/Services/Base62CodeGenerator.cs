using System.Security.Cryptography;
using UrlShortener.Core.Abstractions;

namespace UrlShortener.Core.Services;

/// <summary>
/// Cryptographically random Base62 codes. Random (not sequential) codes prevent
/// enumeration of other users' links. 7 chars = 62^7 ≈ 3.5e12 keyspace.
/// </summary>
public sealed class Base62CodeGenerator : ICodeGenerator
{
    internal const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public string Generate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 4);
        return RandomNumberGenerator.GetString(Alphabet, length);
    }
}
