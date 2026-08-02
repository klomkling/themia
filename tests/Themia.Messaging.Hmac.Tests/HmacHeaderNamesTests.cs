using Xunit;

namespace Themia.Messaging.Hmac.Tests;

// F5 (final whole-branch review): the five derived names used to be captured once from Prefix in field
// initializers, so `names with { Prefix = "X-Foo-" }` compiled and produced a record whose Prefix changed
// but whose Timestamp/Signature/KeyId/Scheme/Origin stayed derived from the OLD prefix — internally
// inconsistent state a `with` expression should never be able to produce. No security bypass (header
// names are unsigned, so a mismatch only fails closed), but a footgun. Computed properties fix it.
public class HmacHeaderNamesTests
{
    [Fact]
    public void With_ShouldProduceHeaderNames_DerivedFromTheNewPrefix()
    {
        var original = new HmacHeaderNames("X-Foo-");

        var changed = original with { Prefix = "X-Bar-" };

        Assert.Equal("X-Bar-", changed.Prefix);
        Assert.Equal("X-Bar-Timestamp", changed.Timestamp);
        Assert.Equal("X-Bar-Signature", changed.Signature);
        Assert.Equal("X-Bar-Key-Id", changed.KeyId);
        Assert.Equal("X-Bar-Scheme", changed.Scheme);
        Assert.Equal("X-Bar-Origin", changed.Origin);
    }

    [Fact]
    public void With_ShouldNotMutateTheOriginalInstance()
    {
        var original = new HmacHeaderNames("X-Foo-");

        _ = original with { Prefix = "X-Bar-" };

        Assert.Equal("X-Foo-", original.Prefix);
        Assert.Equal("X-Foo-Timestamp", original.Timestamp);
    }

    [Fact]
    public void Constructor_ShouldDeriveAllFiveNames_FromThePrefix()
    {
        var names = new HmacHeaderNames("X-Themia-");

        Assert.Equal("X-Themia-Timestamp", names.Timestamp);
        Assert.Equal("X-Themia-Signature", names.Signature);
        Assert.Equal("X-Themia-Key-Id", names.KeyId);
        Assert.Equal("X-Themia-Scheme", names.Scheme);
        Assert.Equal("X-Themia-Origin", names.Origin);
    }
}
