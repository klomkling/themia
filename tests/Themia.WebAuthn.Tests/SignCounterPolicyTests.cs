using Themia.WebAuthn;
using Xunit;

namespace Themia.WebAuthn.Tests;

/// <summary>
/// WebAuthn §7.2 step 21. The authenticator increments a counter on every assertion, so a counter that
/// does not move forward means two authenticators are answering for one credential — the credential has
/// been cloned. The library hands the value over and says nothing about it; deciding is the relying
/// party's job, and most integrations never do, because login succeeds either way.
/// </summary>
public sealed class SignCounterPolicyTests
{
    [Theory]
    [InlineData(0u, 1u)]      // first use
    [InlineData(41u, 42u)]    // ordinary increment
    [InlineData(41u, 9000u)]  // a large jump is fine: the user signed in elsewhere
    public void Moving_forward_is_accepted(uint stored, uint presented)
        => Assert.True(SignCounterPolicy.IsAcceptable(stored, presented));

    [Theory]
    [InlineData(42u, 42u)]  // did not move: two devices at the same count
    [InlineData(42u, 41u)]  // went backwards: an older clone answered
    [InlineData(42u, 0u)]
    public void Not_moving_forward_is_rejected(uint stored, uint presented)
        => Assert.False(SignCounterPolicy.IsAcceptable(stored, presented));

    [Fact]
    public void An_authenticator_that_does_not_count_is_not_treated_as_cloned()
    {
        // Both zero means the authenticator does not implement the counter at all — permitted by the
        // spec, and common on platform authenticators. Rejecting it would lock out every such user
        // on their second sign-in.
        Assert.True(SignCounterPolicy.IsAcceptable(storedSignCount: 0, presentedSignCount: 0));
    }
}
