using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>Which primary check a login passed before the second factor is asked for.</summary>
public enum SecondFactorMethod
{
    /// <summary>Username/e-mail/phone and password.</summary>
    Password,

    /// <summary>A one-time code sent to a confirmed phone number.</summary>
    Otp,

    /// <summary>An external identity provider.</summary>
    External,
}

/// <summary>What the app's policy says about a user who has just passed the primary check.</summary>
public enum SecondFactorRequirement
{
    /// <summary>No second factor: the login completes as before.</summary>
    NotRequired,

    /// <summary>The user has a second factor and must prove it.</summary>
    Verify,

    /// <summary>The user must have a second factor and has none yet: enrol one, then prove it.</summary>
    Enrol,
}

/// <summary>The stage a pending login is waiting at.</summary>
public enum SecondFactorStage
{
    /// <summary>The user must prove a second factor they already have.</summary>
    Verify,

    /// <summary>The user must enrol a second factor first.</summary>
    Enrol,
}

/// <summary>What the policy is asked about.</summary>
/// <param name="UserId">The user who passed the primary check.</param>
/// <param name="TenantId">The user's tenant, or null for a platform user.</param>
/// <param name="Method">The primary check they passed.</param>
/// <param name="Provider">The external provider key when <paramref name="Method"/> is <see cref="SecondFactorMethod.External"/>; otherwise null.</param>
public readonly record struct SecondFactorContext(Guid UserId, TenantId? TenantId, SecondFactorMethod Method, string? Provider = null);

/// <summary>The app's decision whether a login needs a second factor. Evaluated after the primary check
/// succeeded and before anything is issued, in all three flows (password, one-time code, external).</summary>
/// <remarks>
/// The default registration answers <see cref="SecondFactorRequirement.NotRequired"/> for everyone. An
/// exception from the policy is not caught: the login stops with it and nothing is issued. Code that mints
/// sessions without these flows (the public token services) must call the policy itself.
/// </remarks>
public interface ISecondFactorPolicy
{
    /// <summary>Decides whether the user must pass a second factor.</summary>
    /// <param name="context">The user and the primary check they passed.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The requirement.</returns>
    ValueTask<SecondFactorRequirement> EvaluateAsync(SecondFactorContext context, CancellationToken cancellationToken = default);
}

/// <summary>A login that passed the primary check and is waiting for the second factor.</summary>
/// <param name="UserId">The user.</param>
/// <param name="Stage">Whether to verify an existing factor or enrol one first.</param>
/// <param name="Ticket">The opaque single-use ticket the app hands back to <see cref="ISecondFactorCompletion"/>.
/// Treat it as a secret: it is not logged by Themia.</param>
/// <param name="ExpiresAt">When the ticket stops being accepted.</param>
public readonly record struct PendingSecondFactor(Guid UserId, SecondFactorStage Stage, string Ticket, DateTimeOffset ExpiresAt);

/// <summary>The data a ticket carries about the login it stands for.</summary>
/// <param name="UserId">The user.</param>
/// <param name="TenantId">The user's tenant, or null for a platform user.</param>
/// <param name="Method">The primary check that was passed.</param>
/// <param name="Provider">The external provider key, for <see cref="SecondFactorMethod.External"/>.</param>
/// <param name="WasCreated">External login only: the user was provisioned by this login.</param>
/// <param name="WasLinked">External login only: a link was created by this login.</param>
/// <param name="SecurityStamp">The user's security stamp when the ticket was issued.</param>
/// <param name="Stage">The stage the ticket was issued for.</param>
public sealed record SecondFactorTicketData(
    Guid UserId,
    TenantId? TenantId,
    SecondFactorMethod Method,
    string? Provider,
    bool WasCreated,
    bool WasLinked,
    string SecurityStamp,
    SecondFactorStage Stage);

/// <summary>Issues, redeems and burns the tickets of pending logins. Replaceable through DI.</summary>
public interface ISecondFactorTicketService
{
    /// <summary>Issues a ticket for a login that must pass a second factor.</summary>
    /// <param name="data">What the ticket stands for.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The pending login, carrying the ticket.</returns>
    Task<PendingSecondFactor> IssueAsync(SecondFactorTicketData data, CancellationToken cancellationToken = default);

    /// <summary>Consumes a ticket: it is valid once, until it expires.</summary>
    /// <param name="ticket">The ticket from <see cref="PendingSecondFactor.Ticket"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The ticket's data, or null when it is unknown, expired, tampered with or already used.</returns>
    Task<SecondFactorTicketData?> ConsumeAsync(string ticket, CancellationToken cancellationToken = default);
}

/// <summary>How a completion ended.</summary>
public enum SecondFactorCompletionOutcome
{
    /// <summary>The login completed: tokens were issued.</summary>
    Completed,

    /// <summary>The ticket is unknown, expired, used, or the user can no longer sign in (inactive, locked, changed security stamp, method switched off).</summary>
    Refused,

    /// <summary>A hook denied the login at completion; nothing was issued and the ticket is burnt.</summary>
    Denied,
}

/// <summary>The result of <see cref="ISecondFactorCompletion.CompleteAsync"/>.</summary>
public readonly record struct SecondFactorCompletionResult
{
    private SecondFactorCompletionResult(
        SecondFactorCompletionOutcome outcome, AuthTokens? tokens, SecondFactorMethod? method, string? provider, bool wasCreated, bool wasLinked)
    {
        Outcome = outcome; Tokens = tokens; Method = method; Provider = provider; WasCreated = wasCreated; WasLinked = wasLinked;
    }

    /// <summary>How the completion ended.</summary>
    public SecondFactorCompletionOutcome Outcome { get; }

    /// <summary>The issued tokens when <see cref="Outcome"/> is <see cref="SecondFactorCompletionOutcome.Completed"/>; otherwise null.</summary>
    public AuthTokens? Tokens { get; }

    /// <summary>The primary check the completed login passed; null unless completed.</summary>
    public SecondFactorMethod? Method { get; }

    /// <summary>The external provider key for an external login; otherwise null.</summary>
    public string? Provider { get; }

    /// <summary>External login only: the user was provisioned by this login.</summary>
    public bool WasCreated { get; }

    /// <summary>External login only: a link was created by this login.</summary>
    public bool WasLinked { get; }

    /// <summary>Whether tokens were issued.</summary>
    public bool Succeeded => Outcome == SecondFactorCompletionOutcome.Completed;

    /// <summary>Creates a completed result.</summary>
    public static SecondFactorCompletionResult Completed(AuthTokens tokens, SecondFactorMethod method, string? provider, bool wasCreated, bool wasLinked) =>
        new(SecondFactorCompletionOutcome.Completed, tokens, method, provider, wasCreated, wasLinked);

    /// <summary>Creates a refused result.</summary>
    public static SecondFactorCompletionResult Refused() => new(SecondFactorCompletionOutcome.Refused, null, null, null, false, false);

    /// <summary>Creates a denied result.</summary>
    public static SecondFactorCompletionResult Denied() => new(SecondFactorCompletionOutcome.Denied, null, null, null, false, false);
}

/// <summary>Finishes a login that stopped at <see cref="PendingSecondFactor"/> once the app has verified
/// (or enrolled and confirmed) the second factor. Themia never sees the factor; the app does.</summary>
public interface ISecondFactorCompletion
{
    /// <summary>Redeems the ticket and issues the session, repeating the original flow's gates.</summary>
    /// <param name="ticket">The ticket from <see cref="PendingSecondFactor.Ticket"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The completion result.</returns>
    Task<SecondFactorCompletionResult> CompleteAsync(string ticket, CancellationToken cancellationToken = default);

    /// <summary>Burns a ticket without completing, for example after too many wrong codes.</summary>
    /// <param name="ticket">The ticket to burn.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RejectAsync(string ticket, CancellationToken cancellationToken = default);
}
