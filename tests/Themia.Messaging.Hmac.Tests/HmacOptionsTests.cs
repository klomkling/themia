using Xunit;

namespace Themia.Messaging.Hmac.Tests;

public class HmacOptionsTests
{
    // Last-write-wins on a peer registry would silently discard one side's keys — the symptom is
    // intermittent 401s, not a startup error. A duplicate name must fail loudly instead.
    [Fact]
    public void AddPeer_ShouldThrow_WhenNameIsAlreadyRegistered()
    {
        var options = new HmacOptions();
        options.AddPeer("peer", p =>
        {
            p.SignWith("out-1", "secret");
            p.Accept("in-1", "secret");
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddPeer("peer", p =>
            {
                p.SignWith("out-2", "other-secret");
                p.Accept("in-2", "other-secret");
            }));

        Assert.Contains("peer", ex.Message, StringComparison.Ordinal);
    }
}
