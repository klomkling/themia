using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Tests;

public class StringTruncateExtensionsTests
{
    [Fact]
    public void Truncate_ShortensLongStrings()
    {
        Assert.Equal("abc", "abcdef".Truncate(3));
    }

    [Fact]
    public void Truncate_LeavesShortStrings()
    {
        Assert.Equal("ab", "ab".Truncate(5));
    }

    [Fact]
    public void Truncate_HandlesNull()
    {
        Assert.Null(((string?)null).Truncate(5));
    }
}
