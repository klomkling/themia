using System.Security.Claims;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>How an app-minted session ended.</summary>
public enum IssueOutcome
{
    /// <summary>The session was issued.</summary>
    Issued,

    /// <summary>The user is not active.</summary>
    Inactive,

    /// <summary>The user is locked out.</summary>
    LockedOut,

    /// <summary>The user is not visible in the expected tenant (or is a platform user while platform login is off). Deliberately
    /// does not say which.</summary>
    TenantMismatch,

    /// <summary>The app's <see cref="ISecondFactorPolicy"/> requires a second factor first: nothing was issued; see <see cref="IssueResult.SecondFactor"/>.</summary>
    SecondFactorRequired,

    /// <summary>A renewal named a session that is not a live session of this user.</summary>
    SessionNotLive,

    /// <summary>The second-factor ticket is unknown, expired, used, not for this user and channel, or the user changed since it was issued.</summary>
    TicketInvalid,
}

/// <summary>The result of <see cref="IAuthTokenIssuer"/>.</summary>
public readonly record struct IssueResult
{
    private IssueResult(IssueOutcome outcome, AuthTokens? tokens, Guid? sessionId, PendingSecondFactor? secondFactor)
    {
        Outcome = outcome; Tokens = tokens; SessionId = sessionId; SecondFactor = secondFactor;
    }

    /// <summary>How the call ended.</summary>
    public IssueOutcome Outcome { get; }

    /// <summary>The issued tokens when <see cref="Outcome"/> is <see cref="IssueOutcome.Issued"/>; otherwise null. A renewal has an
    /// access token only: its <see cref="AuthTokens.RefreshToken"/> is empty.</summary>
    public AuthTokens? Tokens { get; }

    /// <summary>The session id (the <c>sid</c> claim) when issued; otherwise null.</summary>
    public Guid? SessionId { get; }

    /// <summary>The pending second factor when <see cref="Outcome"/> is <see cref="IssueOutcome.SecondFactorRequired"/>; otherwise null.</summary>
    public PendingSecondFactor? SecondFactor { get; }

    /// <summary>Whether a session was issued.</summary>
    public bool Succeeded => Outcome == IssueOutcome.Issued;

    /// <summary>Creates an issued result.</summary>
    public static IssueResult Issued(AuthTokens tokens, Guid sessionId) => new(IssueOutcome.Issued, tokens, sessionId, null);

    /// <summary>Creates a refused result.</summary>
    /// <param name="outcome">Any outcome other than <see cref="IssueOutcome.Issued"/> and <see cref="IssueOutcome.SecondFactorRequired"/>.</param>
    public static IssueResult Refused(IssueOutcome outcome) =>
        outcome is IssueOutcome.Issued or IssueOutcome.SecondFactorRequired
            ? throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Use Issued or RequiresSecondFactor for this outcome.")
            : new(outcome, null, null, null);

    /// <summary>Creates a result for a session that must pass a second factor first.</summary>
    public static IssueResult RequiresSecondFactor(PendingSecondFactor pending) => new(IssueOutcome.SecondFactorRequired, null, null, pending);
}

/// <summary>What an app tells the issuer about the session it is minting.</summary>
/// <param name="ExpectedTenant">The tenant the user must belong to, or null for a platform user. The user is looked up in this scope.</param>
/// <param name="Channel">The app's label for the channel (for example <c>federation</c>): 1 to 64 characters of <c>a-z 0-9 . _ -</c>.
/// It goes into tickets, logs and audit; do not key a second-factor policy on it.</param>
/// <param name="AppClaims">Extra claims for the principal, replacing any claim of the same type already on it (so a session-start claim
/// can be carried forward). A reserved claim type (<see cref="ReservedClaimTypes"/>) is refused with <see cref="ArgumentException"/>
/// before anything is issued.</param>
public sealed record SessionRequest(TenantId? ExpectedTenant, string Channel, IReadOnlyCollection<Claim>? AppClaims = null);

/// <summary>The one guarded way for an app to mint a session outside the Themia login flows. It reloads the user, checks active, lockout
/// and tenant, asks the <see cref="ISecondFactorPolicy"/>, sets the <c>sid</c> claim, issues the refresh token first and revokes it if
/// the access token cannot be issued. Use it instead of calling <see cref="IRefreshTokenService"/>/<see cref="IAccessTokenService"/> directly.</summary>
public interface IAuthTokenIssuer
{
    /// <summary>Starts a new session for a user the app has already authenticated by its own means.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="request">The tenant, channel and app claims.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The issued session, or why not (including a pending second factor).</returns>
    Task<IssueResult> StartSessionAsync(Guid userId, SessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Finishes a session that stopped at <see cref="IssueOutcome.SecondFactorRequired"/>, after the app verified the factor.
    /// This is the only completion path for an app-channel ticket (<see cref="ISecondFactorCompletion"/> refuses them).</summary>
    /// <param name="userId">The user whose factor the app just verified.</param>
    /// <param name="ticket">The ticket from <see cref="PendingSecondFactor.Ticket"/>.</param>
    /// <param name="request">The same tenant, channel and app claims as the start.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<IssueResult> CompleteSessionAsync(Guid userId, string ticket, SessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Issues a new access token for a session that already exists, without a new refresh family and without asking the
    /// second-factor policy (the session passed it when it was created). Refused unless <paramref name="sessionId"/> is a live
    /// session of this user.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="sessionId">The session (refresh family) id.</param>
    /// <param name="request">The tenant, channel and app claims.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<IssueResult> RenewSessionAsync(Guid userId, Guid sessionId, SessionRequest request, CancellationToken cancellationToken = default);
}
