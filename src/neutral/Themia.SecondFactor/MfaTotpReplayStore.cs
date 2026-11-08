using Themia.Totp;

namespace Themia.SecondFactor;

/// <summary>
/// The <see cref="ITotpReplayStore"/> for the second-factor layer. The secret id is the method row id, which the service
/// only ever takes from a tenant-scoped lookup. A pending enrolment (id prefix <c>pending:</c>) has no row yet, so it
/// always passes; its matched step is stored with the new method.
/// </summary>
internal sealed class MfaTotpReplayStore(IMfaMethodStore methods, TimeProvider time) : ITotpReplayStore
{
    public async ValueTask<bool> TryAdvanceAsync(string secretId, long matchedStep, CancellationToken ct = default)
    {
        if (secretId.StartsWith("pending:", StringComparison.Ordinal))
        {
            return true;
        }

        return Guid.TryParse(secretId, out var methodId)
            && await methods.TryAdvanceTotpStepByMethodAsync(methodId, matchedStep, time.GetUtcNow(), ct).ConfigureAwait(false);
    }
}
