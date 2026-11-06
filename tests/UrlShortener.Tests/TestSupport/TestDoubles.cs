using UrlShortener.Core.Abstractions;

namespace UrlShortener.Tests.TestSupport;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>Returns a scripted sequence of codes, to exercise collision handling deterministically.</summary>
public sealed class SequenceCodeGenerator : ICodeGenerator
{
    private readonly Queue<string> _codes;

    public SequenceCodeGenerator(params string[] codes) => _codes = new Queue<string>(codes);

    public int Calls { get; private set; }

    public string Generate(int length)
    {
        Calls++;
        return _codes.Count > 1 ? _codes.Dequeue() : _codes.Peek();
    }
}
