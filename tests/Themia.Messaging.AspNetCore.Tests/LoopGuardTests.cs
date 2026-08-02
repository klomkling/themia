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

    // Retargeted, not retired. This used to assert the guard went INACTIVE on a blank ownOrigin — the
    // documented "leave Origin unset to disable the loop guard" escape hatch. That hatch moved to
    // VerificationOptions.DisableLoopGuard, which the filter checks before calling here, so a blank
    // origin reaching this method is now a programming error rather than a configuration choice, and
    // silently returning false would hide it.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsLoopback_ShouldThrow_WhenOwnOriginIsBlank(string? ownOrigin)
    {
        var headers = new Dictionary<string, string?> { [Names.Origin] = "self" };

        Assert.ThrowsAny<ArgumentException>(() => LoopGuard.IsLoopback(headers, Names, ownOrigin!));
    }
}
