using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Tests;

public class ExceptionHashTests
{
    [Fact]
    public void Compute_IsDeterministic_ForSameTypeAndStack()
    {
        var h1 = ExceptionHash.Compute("System.InvalidOperationException", "at Foo.Bar()");
        var h2 = ExceptionHash.Compute("System.InvalidOperationException", "at Foo.Bar()");

        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length); // SHA-256 hex
    }

    [Fact]
    public void Compute_Differs_ForDifferentStacks()
    {
        var h1 = ExceptionHash.Compute("System.Exception", "at A()");
        var h2 = ExceptionHash.Compute("System.Exception", "at B()");

        Assert.NotEqual(h1, h2);
    }
}
