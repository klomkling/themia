using Themia.Messaging.Hmac;

using Xunit;

namespace Themia.Messaging.AspNetCore.Tests;

public class LoopGuardTests
{
    private static readonly HmacHeaderNames Names = new(HmacHeaderNames.DefaultPrefix);

    [Fact]
    public void IsLoopback_ShouldReturnTrue_WhenOriginHeaderMatchesOwnOrigin()
    {
        var headers = new Dictionary<string, string?> { [Names.Origin] = "self" };

        Assert.True(LoopGuard.IsLoopback(headers, Names, "self"));
    }

    [Fact]
    public void IsLoopback_ShouldReturnFalse_WhenOriginHeaderDiffersFromOwnOrigin()
    {
        var headers = new Dictionary<string, string?> { [Names.Origin] = "someone-else" };

        Assert.False(LoopGuard.IsLoopback(headers, Names, "self"));
    }

    [Fact]
    public void IsLoopback_ShouldReturnFalse_WhenOriginHeaderIsAbsent()
    {
        var headers = new Dictionary<string, string?>();

        Assert.False(LoopGuard.IsLoopback(headers, Names, "self"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsLoopback_ShouldReturnFalse_WhenOwnOriginIsNotConfigured(string? ownOrigin)
    {
        var headers = new Dictionary<string, string?> { [Names.Origin] = "self" };

        Assert.False(LoopGuard.IsLoopback(headers, Names, ownOrigin));
    }
}
