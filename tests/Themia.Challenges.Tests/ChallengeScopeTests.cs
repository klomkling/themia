using Themia.Challenges;
using Xunit;

namespace Themia.Challenges.Tests;

public class ChallengeScopeTests
{
    [Theory]
    [InlineData("+15551234567")]
    [InlineData("someone@example.com")]
    public void ToString_ShouldNotExposeTheFullKey(string key)
    {
        var scope = new ChallengeScope(key, "login", "tenant-1");

        var rendered = scope.ToString();

        Assert.DoesNotContain(key, rendered, StringComparison.Ordinal);
        Assert.Contains("login", rendered, StringComparison.Ordinal);
        Assert.Contains("tenant-1", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ShouldKeepLastFourCharactersOfTheKeyForCorrelation()
    {
        var scope = new ChallengeScope("+15551234567", "login");

        var rendered = scope.ToString();

        Assert.Contains("4567", rendered, StringComparison.Ordinal);
    }

    // Rejected at the boundary, not at the store. An over-long key that reaches storage is truncated to
    // the column width, which silently makes it a DIFFERENT rate-limit bucket than the caller intended —
    // and the per-key bucket is what bounds the SMS bill. Two dialects hit this property independently.
    [Fact]
    public void Constructor_ShouldRejectAKeyLongerThanTheColumn()
    {
        var tooLong = new string('x', ChallengeScope.MaxKeyLength + 1);

        var ex = Assert.Throws<ArgumentException>(() => new ChallengeScope(tooLong, "login"));

        Assert.Equal("Key", ex.ParamName);
    }

    [Fact]
    public void Constructor_ShouldAcceptAKeyExactlyAtTheColumnWidth()
    {
        var exact = new string('x', ChallengeScope.MaxKeyLength);

        Assert.Equal(exact, new ChallengeScope(exact, "login").Key);
    }

    [Fact]
    public void Constructor_ShouldRejectAnOverLongPurposeAndTenantId()
    {
        var longPurpose = new string('p', ChallengeScope.MaxPurposeLength + 1);
        var longTenant = new string('t', ChallengeScope.MaxTenantIdLength + 1);

        Assert.Equal("Purpose", Assert.Throws<ArgumentException>(() => new ChallengeScope("+66811112222", longPurpose)).ParamName);
        Assert.Equal("TenantId", Assert.Throws<ArgumentException>(() => new ChallengeScope("+66811112222", "login", longTenant)).ParamName);
    }

    // A null tenant is a platform-level challenge and must stay allowed.
    [Fact]
    public void Constructor_ShouldAllowANullTenantId()
    {
        Assert.Null(new ChallengeScope("+66811112222", "login").TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ShouldRejectABlankKey(string key)
    {
        Assert.Throws<ArgumentException>(() => new ChallengeScope(key, "login"));
    }

    // The reason validation lives in the init accessor rather than only on the positional parameter:
    // a record's `with` runs the copy constructor, so a constructor-only guard would be bypassed here.
    [Fact]
    public void With_ShouldStillRejectAnOverLongKey()
    {
        var scope = new ChallengeScope("+66811112222", "login");
        var tooLong = new string('x', ChallengeScope.MaxKeyLength + 1);

        Assert.Throws<ArgumentException>(() => scope with { Key = tooLong });
    }
}
