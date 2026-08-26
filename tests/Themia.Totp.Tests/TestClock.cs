namespace Themia.Totp.Tests;

/// <summary>
/// A clock a test can set to any instant, forward or back.
/// </summary>
/// <remarks>
/// <c>FakeTimeProvider</c> refuses to go backwards ("Cannot go back in time"), and several of these
/// tests need to mint a code at one instant and verify it at an earlier one — which is exactly the
/// clock-skew case a verification window exists for.
/// </remarks>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset value) => _now = value;

    public void AdvanceSeconds(int seconds) => _now = _now.AddSeconds(seconds);
}
