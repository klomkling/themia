namespace Themia.Totp;

/// <summary>
/// Records that a TOTP code has been used, so it cannot be used again inside its own step.
/// </summary>
/// <remarks>
/// This is the reason <c>Themia.Totp</c> exists. A TOTP code stays valid for its entire step — 30
/// seconds by default, and up to 90 with a ±1-step tolerance — so an implementation that only asks
/// "does this code match this window" is self-consistently correct and still lets an observer replay
/// the code for the rest of that window. Every test written from the RFC's description passes without
/// this guard.
/// <para>
/// <b>No implementation is registered by default.</b> An in-memory store holds nothing on a second
/// instance, so every verification would report correct while the replay window stays open — the guard
/// would appear to work with a green suite on either side. <c>AddThemiaTotp</c> therefore requires an
/// explicit implementation and fails at registration without one.
/// </para>
/// </remarks>
public interface ITotpReplayStore
{
    /// <summary>
    /// Atomically records <paramref name="matchedStep"/> as consumed for <paramref name="secretId"/>,
    /// reporting whether it was still free.
    /// </summary>
    /// <param name="secretId">
    /// Opaque identifier for the credential the code belongs to — a user id, a credential id, whatever
    /// the caller uses. It is the caller's to choose and <b>must not be the shared secret itself</b>;
    /// this package never sees or stores secrets at rest.
    /// </param>
    /// <param name="matchedStep">
    /// The time step the submitted code actually matched — <b>not</b> the current step. With a ±1
    /// tolerance a code minted for step S is accepted while the clock reads S-1, S or S+1; recording
    /// the current step would let the same code through again one step later, so the guard would pass
    /// its own test without closing the window.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the step was free and is now consumed; <see langword="false"/> when
    /// it had already been used.
    /// </returns>
    /// <remarks>
    /// One atomic operation rather than a check followed by a record: split in two, concurrent
    /// verifications of the same code race between the check and the write, and both are admitted.
    /// An implementation must make this test-and-set atomic in whatever store it uses.
    /// </remarks>
    ValueTask<bool> TryConsumeAsync(string secretId, long matchedStep, CancellationToken ct = default);
}
