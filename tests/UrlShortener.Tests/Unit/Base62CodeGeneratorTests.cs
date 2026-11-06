using UrlShortener.Core.Services;

namespace UrlShortener.Tests.Unit;

public class Base62CodeGeneratorTests
{
    [Fact]
    public void Generate_ProducesRequestedLengthFromAlphabet()
    {
        var code = new Base62CodeGenerator().Generate(7);

        Assert.Equal(7, code.Length);
        Assert.All(code, c => Assert.Contains(c, Base62CodeGenerator.Alphabet));
    }

    [Fact]
    public void Generate_IsEffectivelyUnique()
    {
        var generator = new Base62CodeGenerator();
        var codes = Enumerable.Range(0, 10_000).Select(_ => generator.Generate(7)).ToHashSet();

        Assert.Equal(10_000, codes.Count);
    }

    [Fact]
    public void Generate_RejectsTooShortCodes() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Base62CodeGenerator().Generate(3));
}
