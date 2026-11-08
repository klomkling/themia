using Themia.WebAuthn;

namespace Themia.SecondFactor;

/// <summary>
/// The <see cref="IWebAuthnChallengeStore"/> for the second-factor layer: ceremonies live in <see cref="IMfaCeremonyStore"/>,
/// bound to the tenant, user and purpose in <see cref="PasskeyCeremonyContext"/>. Calling it outside a service flow is a bug.
/// </summary>
internal sealed class CeremonyBackedWebAuthnStore(IMfaCeremonyStore ceremonies) : IWebAuthnChallengeStore
{
    public async ValueTask StoreAsync(string challengeId, string optionsJson, TimeSpan ttl, CancellationToken ct = default)
    {
        var frame = PasskeyCeremonyContext.Get() ?? throw NoContext();
        await ceremonies.StoreAsync(challengeId, optionsJson, frame.TenantId, frame.UserId, frame.Purpose, ttl, ct).ConfigureAwait(false);
    }

    public async ValueTask<string?> TryConsumeAsync(string challengeId, CancellationToken ct = default)
    {
        var frame = PasskeyCeremonyContext.Get() ?? throw NoContext();
        var ceremony = await ceremonies.TryConsumeAsync(challengeId, frame.TenantId, frame.UserId, frame.Purpose, ct).ConfigureAwait(false);
        return ceremony?.OptionsJson;
    }

    private static InvalidOperationException NoContext() =>
        new("A WebAuthn ceremony was stored or consumed outside ISecondFactorService; it must run inside a service call so it is bound to a user and purpose.");
}
