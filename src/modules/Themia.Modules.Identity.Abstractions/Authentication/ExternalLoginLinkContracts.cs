using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>One external identity linked to a user.</summary>
/// <param name="Provider">The provider key, lower-cased (e.g. <c>"line"</c>, <c>"telegram"</c>).</param>
/// <param name="Subject">The provider's stable subject for the identity.</param>
/// <param name="LinkedAt">When the link was created. The earliest one is a reasonable default channel.</param>
public sealed record ExternalLoginInfo(string Provider, string Subject, DateTimeOffset LinkedAt);

/// <summary>Why <see cref="IExternalLoginLinkService.LinkAsync"/> did or did not link.</summary>
/// <remarks>
/// An enum so a caller's exhaustive <c>switch</c> breaks the build when a state is added, instead of a new
/// case compiling silently into whatever branch handled "not linked".
/// </remarks>
public enum ExternalLoginLinkOutcome
{
    /// <summary>A new link was created.</summary>
    Linked,

    /// <summary>The identity was already linked to this user. Nothing was written.</summary>
    AlreadyLinkedToUser,

    /// <summary>
    /// The identity belongs to a different user. Nothing was written — an identity is never moved between
    /// users, because that is an account takeover by another name.
    /// </summary>
    LinkedToAnotherUser,

    /// <summary>No user matched the id in the ambient scope.</summary>
    UserNotFound,

    /// <summary>
    /// The user is deactivated or locked out. Nothing was written, for the same reason
    /// <see cref="IExternalLoginService.ResolveOrProvisionAsync"/> refuses: a later reactivation would
    /// otherwise inherit a sign-in method nobody approved.
    /// </summary>
    UserInactive,

    /// <summary>An <see cref="IUserLifecycleHooks"/> implementation refused. See the reason.</summary>
    Refused,
}

/// <summary>The outcome of <see cref="IExternalLoginLinkService.LinkAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">The hook's reason when <paramref name="Outcome"/> is <see cref="ExternalLoginLinkOutcome.Refused"/>; otherwise null.</param>
public readonly record struct ExternalLoginLinkResult(ExternalLoginLinkOutcome Outcome, string? Reason = null)
{
    /// <summary>Whether the identity is linked to the user now — newly or already.</summary>
    public bool IsLinked => Outcome is ExternalLoginLinkOutcome.Linked or ExternalLoginLinkOutcome.AlreadyLinkedToUser;
}

/// <summary>Why <see cref="IExternalLoginLinkService.UnlinkAsync"/> did or did not unlink.</summary>
public enum ExternalLoginUnlinkOutcome
{
    /// <summary>Every link the user held for the provider was removed.</summary>
    Unlinked,

    /// <summary>The user held no link for the provider. Nothing was written.</summary>
    NotLinked,

    /// <summary>No user matched the id in the ambient scope.</summary>
    UserNotFound,

    /// <summary>An <see cref="IUserLifecycleHooks"/> implementation refused. See the reason.</summary>
    Refused,
}

/// <summary>The outcome of <see cref="IExternalLoginLinkService.UnlinkAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">The hook's reason when <paramref name="Outcome"/> is <see cref="ExternalLoginUnlinkOutcome.Refused"/>; otherwise null.</param>
public readonly record struct ExternalLoginUnlinkResult(ExternalLoginUnlinkOutcome Outcome, string? Reason = null);

/// <summary>
/// Manages the external identities linked to an existing user: attach, detach, list, and look up.
/// </summary>
/// <remarks>
/// A separate service from <see cref="IExternalLoginService"/> on purpose. That one is the
/// bring-your-own seam of the external sign-in flow — an adopter can run the flow with their own
/// implementation and no Identity persistence at all — while every operation here reads or writes
/// <c>identity.external_logins</c>. Folding these into it would force every BYO adopter to implement
/// operations over a table they do not have.
/// <para>
/// All operations act on the <b>ambient partition</b>: the links of the current tenant, or the platform's
/// under a platform scope. That is where a new link lands — the data layer stamps the ambient tenant on
/// every insert — and what this scope's sign-in consults first. So a platform user linked from a tenant
/// scope gets that tenant's link, and listing or unlinking from a tenant scope never reaches the platform's
/// own links. Ownership alone looks further: with
/// <see cref="IdentityModuleOptions.AllowPlatformLogin"/> on, an identity held in the platform partition is
/// answered <see cref="ExternalLoginLinkOutcome.LinkedToAnotherUser"/>, because this scope's sign-in would
/// resolve it to that owner.
/// </para>
/// <para>
/// <b>Linking and unlinking do not end any session.</b> A caller unlinking a compromised identity should
/// follow with <see cref="IRefreshTokenService.RevokeAllForUserAsync"/>; sessions are not tagged by the
/// identity that opened them, so nothing else can target the compromised one.
/// </para>
/// </remarks>
public interface IExternalLoginLinkService
{
    /// <summary>Links an external identity to an existing user.</summary>
    /// <param name="userId">The user to link to.</param>
    /// <param name="identity">
    /// The verified external identity. Only <see cref="ExternalIdentity.Provider"/> and
    /// <see cref="ExternalIdentity.Subject"/> are used: this never links by email and never touches the
    /// user's email or its confirmation.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    /// Race-safe at the database: two concurrent links of the same identity to different users produce
    /// exactly one <see cref="ExternalLoginLinkOutcome.Linked"/>; the loser's insert violates the unique
    /// index and is answered <see cref="ExternalLoginLinkOutcome.LinkedToAnotherUser"/>.
    /// <para>
    /// A user may hold more than one identity from the same provider — two Google accounts is legitimate —
    /// so a second link for a provider is not refused here. A consumer allowing one per provider enforces
    /// it in <see cref="IUserLifecycleHooks.OnBeforeLinkExternalLoginAsync"/>.
    /// </para>
    /// </remarks>
    Task<ExternalLoginLinkResult> LinkAsync(
        Guid userId, ExternalIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Removes every link the user holds for a provider.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="provider">The provider to disconnect.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    /// This is how a user without a password locks themselves out. Themia does not refuse the "last"
    /// method itself, because it cannot know every way a consumer's users sign in — a rule like "never
    /// leave a user with no channel" belongs in
    /// <see cref="IUserLifecycleHooks.OnBeforeUnlinkExternalLoginAsync"/>.
    /// </remarks>
    Task<ExternalLoginUnlinkResult> UnlinkAsync(
        Guid userId, string provider, CancellationToken cancellationToken = default);

    /// <summary>The external identities linked to a user, oldest first.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The links; empty when the user has none or does not resolve in scope.</returns>
    Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The external identities linked to each of several users, without a query per user.</summary>
    /// <param name="userIds">Up to <see cref="MaxBatchSize"/> distinct user ids.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An entry only for users holding at least one link; each list oldest first.</returns>
    /// <exception cref="ArgumentOutOfRangeException">More than <see cref="MaxBatchSize"/> distinct ids.</exception>
    /// <remarks>
    /// Named apart from <see cref="GetLoginsAsync"/> rather than overloading it: two overloads that both
    /// default the cancellation token are ambiguous to add to later without breaking callers (RS0026).
    /// <para>
    /// The ambient partition only — the same reach <see cref="GetLoginsAsync"/> has. Ids with no links in this
    /// scope simply have no entry.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ExternalLoginInfo>>> GetLoginsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>The user an external identity is linked to. Never provisions and never links.</summary>
    /// <param name="provider">The provider key.</param>
    /// <param name="subject">The provider subject.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The user, or <see langword="null"/> when the identity is linked to nobody in scope.</returns>
    /// <remarks>
    /// The same lookup <see cref="IExternalLoginService.ResolveOrProvisionAsync"/> performs first,
    /// including the platform fallback under <see cref="IdentityModuleOptions.AllowPlatformLogin"/> — without
    /// the create that follows it. For a caller that must answer "unknown identity" rather than open an
    /// account.
    /// </remarks>
    Task<User?> FindUserByLoginAsync(string provider, string subject, CancellationToken cancellationToken = default);

    /// <summary>The most distinct user ids one <see cref="GetLoginsForUsersAsync"/> call accepts.</summary>
    /// <remarks>Well inside SQL Server's 2,100-parameter limit on an <c>IN</c> list.</remarks>
    const int MaxBatchSize = 1000;
}
