namespace Themia.Totp;

/// <summary>
/// Records the highest time step accepted for a credential, so no code at or below it is usable again.
/// </summary>
/// <remarks>
/// This is the reason <c>Themia.Totp</c> exists. A TOTP code stays valid for its entire step — 30
/// seconds by default, and up to 90 with a ±1-step tolerance — so an implementation that only asks
/// "does this code match this window" is self-consistently correct and still lets an observer replay
/// the code for the rest of that window. Every test written from the RFC's description passes without
/// this guard.
/// <para>
/// <b>The guard is monotonic:</b> a step is accepted only when it is higher than every step already
/// accepted for that credential. Consuming just the matched step is the near-miss — it stops the same
/// code twice and still admits an older captured code after a newer login. See
/// <see cref="TryAdvanceAsync"/>.
/// </para>
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
    /// Atomically advances <paramref name="secretId"/>'s highest accepted step to
    /// <paramref name="matchedStep"/>, reporting whether it moved forward.
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
    /// <see langword="true"/> when <paramref name="matchedStep"/> is <b>strictly greater</b> than every
    /// step previously accepted for <paramref name="secretId"/>, which is now the highest;
    /// <see langword="false"/> otherwise.
    /// </returns>
    /// <remarks>
    /// <b>The contract is monotonic, not set membership.</b> Refusing only the exact step that was used
    /// leaves an older code inside the tolerance window still usable: an observer captures the code for
    /// step S, the real user signs in at S+1, and the captured code is then accepted at step S because
    /// nothing consumed it. Refusing every step at or below the highest one accepted closes that, and
    /// costs an implementation nothing — it stores one row per credential rather than one per step, and
    /// needs no expiry sweep.
    /// <para>
    /// The canonical implementations are a single conditional write:
    /// <c>UPDATE totp_replay SET last_step = @step WHERE secret_id = @id AND last_step &lt; @step</c>
    /// (returning whether a row changed, with an insert for the first use), or a Redis Lua script
    /// comparing and setting in one call.
    /// </para>
    /// <para>
    /// One atomic operation rather than a check followed by a record: split in two, concurrent
    /// verifications of the same code race between the check and the write, and both are admitted.
    /// An implementation must make this compare-and-set atomic in whatever store it uses.
    /// </para>
    /// </remarks>
    ValueTask<bool> TryAdvanceAsync(string secretId, long matchedStep, CancellationToken ct = default);
}
