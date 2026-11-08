namespace Themia.SecondFactor;

/// <summary>
/// Carries (tenant, user, purpose) across the WebAuthn service call, so the ceremony store behind
/// <c>IWebAuthnChallengeStore</c> can bind each ceremony to the user and purpose it was opened for. The WebAuthn
/// service passes only a challenge id and options, with no user.
/// </summary>
internal static class PasskeyCeremonyContext
{
    private static readonly AsyncLocal<Frame?> Current = new();

    internal sealed record Frame(string? TenantId, string UserId, MfaCeremonyPurpose Purpose);

    public static Frame? Get() => Current.Value;

    public static IDisposable Begin(string? tenantId, string userId, MfaCeremonyPurpose purpose)
    {
        var previous = Current.Value;
        Current.Value = new Frame(tenantId, userId, purpose);
        return new Restore(previous);
    }

    private sealed class Restore(Frame? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
