namespace Themia.Challenges;

/// <summary>
/// Verifies a numeric code against the one challenge a caller holds the handle of. A separate interface
/// from <see cref="IChallengeService"/> so adopters' own fakes of that one keep compiling.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists. <see cref="IChallengeService.VerifyAsync"/> finds the challenge by (key, purpose), and the
/// key is usually something a stranger knows, such as an e-mail address. It charges
/// <see cref="ChallengeOptions.VerifyWindow"/> per key before reading anything, and charges the attempt
/// budget of every live challenge of the scope on each wrong code. So a stranger who knows the address can
/// spend both and make the owner's correct code come back <see cref="ChallengeVerifyOutcome.RateLimited"/>
/// or <see cref="ChallengeVerifyOutcome.AttemptsExhausted"/>. A per-IP limiter in the app cannot fix that.
/// </para>
/// <para>
/// The handle is the <see cref="ChallengeIssueResult.ChallengeId"/> of the issue call. Give it to the
/// sender of the issue request only (the response body, or an HttpOnly cookie scoped to the verify path),
/// never in the message that carries the code. It is not an authenticator: the code must still match.
/// It only decides whose budget a guess is charged to. Nothing is charged until the handle resolves to its
/// challenge, and then the charge is per handle, so a stranger who issues a challenge of their own for the
/// same key burns their own handle and nothing else. A handle that does not resolve costs one primary-key
/// lookup and no counter row; bounding garbage handles is the app's per-IP limiter.
/// </para>
/// <para>
/// The brute-force bound is unchanged: guesses are charged to a challenge, and challenges are bounded by
/// issuance (<see cref="PurposeOptions.PerScopeWindow"/> times <see cref="PurposeOptions.MaxAttempts"/>).
/// Set <see cref="PurposeOptions.MaxLiveChallenges"/> to 2 or more for handle flows, so a stranger's issue
/// does not void the owner's live challenge, and set <see cref="PurposeOptions.RequireHandle"/> so no
/// scope-keyed verify is left that could still spend the shared budget.
/// </para>
/// <para>
/// Never pass a client-supplied handle to <see cref="IChallengeService.RefundAsync"/>: it takes the same
/// id, and a refund is a server-side decision. Parse the client's handle with <see cref="Guid.TryParse(string?, out Guid)"/>
/// and answer a parse failure like <see cref="ChallengeVerifyOutcome.NotFound"/> without calling the library.
/// </para>
/// </remarks>
public interface IChallengeHandles
{
    /// <summary>Checks <paramref name="code"/> against the challenge <paramref name="handle"/> names, and consumes it on a match.</summary>
    /// <param name="handle">The <see cref="ChallengeIssueResult.ChallengeId"/> the issue call returned.</param>
    /// <param name="purpose">Asserted against the found challenge.</param>
    /// <param name="code">The submitted code.</param>
    /// <param name="tenantId">Asserted against the found challenge.</param>
    /// <param name="cancellationToken">Cancels the underlying store operations.</param>
    /// <returns>
    /// <see cref="ChallengeVerifyOutcome.NotFound"/> for an unknown handle, a wrong purpose and a wrong tenant alike
    /// (nothing charged); otherwise the state of the handle's own challenge, never of a newer one in the same scope.
    /// A challenge voided by a newer issue (<see cref="PurposeOptions.MaxLiveChallenges"/> of 1) reads as
    /// <see cref="ChallengeVerifyOutcome.Consumed"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException"><paramref name="purpose"/> was never configured via <see cref="ChallengeOptions.ConfigurePurpose"/>.</exception>
    Task<ChallengeVerifyResult> VerifyByHandleAsync(
        Guid handle, string purpose, string code, string? tenantId = null, CancellationToken cancellationToken = default);
}
