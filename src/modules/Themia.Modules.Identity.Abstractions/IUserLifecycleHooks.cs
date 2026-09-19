using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>
/// Lets a consumer observe, and refuse, mutations to a user's credential state.
/// </summary>
/// <remarks>
/// <c>IAuthenticationHooks</c> covers the login lifecycle only, so a consumer holding a rule
/// keyed on credential state — "this account must keep one usable way to sign in", "a deactivated user
/// releases their seat", "you cannot remove the last admin" — could enforce it only by owning every
/// call site, which is the coupling this module exists to remove.
/// <para>
/// <b>Every mutation has a hook, not a chosen few.</b> A seam covering three of seven paths reads as
/// covering all seven: a consumer registers a hook, believes the rule holds, and it silently does not
/// on the paths nobody wired.
/// </para>
/// <para>
/// <b>Contract for implementations — a before-hook runs inside the caller's scope, before the module
/// touches any entity and before its unit of work opens.</b> It must not call <c>SaveChanges</c> and
/// must not open a transaction on the same scoped connection: the module saves immediately after the
/// hook returns, and a hook that has already committed or is holding a transaction on that connection
/// turns a refusal into a deadlock the consumer authored. Read freely; write through your own
/// connection if you must write at all.
/// </para>
/// <para>
/// Every method has a default implementation, so an existing consumer that implements nothing is
/// unaffected, and a consumer that cares about one mutation overrides one method.
/// </para>
/// </remarks>
public interface IUserLifecycleHooks
{
    /// <summary>Called before the email is set or cleared.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="email">The new address, or null when it is being cleared.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    ValueTask<UserMutationDecision> OnBeforeSetEmailAsync(
        Guid userId, string? email, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(UserMutationDecision.Allow());

    /// <summary>Called before the email is marked confirmed.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    ValueTask<UserMutationDecision> OnBeforeConfirmEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(UserMutationDecision.Allow());

    /// <summary>Called before the phone number is set or cleared.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="phoneNumber">The new number, or null when it is being cleared.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    /// <remarks>
    /// Setting a new number clears its confirmation, so this is the hook that stands between an account
    /// relying on phone sign-in and being locked out of it.
    /// </remarks>
    ValueTask<UserMutationDecision> OnBeforeSetPhoneNumberAsync(
        Guid userId, string? phoneNumber, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(UserMutationDecision.Allow());

    /// <summary>Called before the phone number is marked confirmed.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    ValueTask<UserMutationDecision> OnBeforeConfirmPhoneNumberAsync(
        Guid userId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(UserMutationDecision.Allow());

    /// <summary>Called before the password hash is replaced.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    ValueTask<UserMutationDecision> OnBeforeSetPasswordAsync(
        Guid userId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(UserMutationDecision.Allow());

    /// <summary>Called before the account is activated or deactivated.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="isActive">The state being set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    ValueTask<UserMutationDecision> OnBeforeSetActiveAsync(
        Guid userId, bool isActive, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(UserMutationDecision.Allow());

    /// <summary>Called before the user is deleted.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    /// <remarks>
    /// A consumer whose own rows reference the user through a cascade gets its refusal here, with a
    /// reason — instead of a foreign-key violation surfacing from the module's save.
    /// </remarks>
    ValueTask<UserMutationDecision> OnBeforeDeleteAsync(
        Guid userId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(UserMutationDecision.Allow());

    /// <summary>Called before an external identity is linked to the user.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="provider">The provider key, lower-cased.</param>
    /// <param name="subject">The provider subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    /// <remarks>
    /// Where a consumer allowing one identity per provider enforces it — the module permits several,
    /// because two accounts at the same provider are legitimate in general. That rule needs the user's
    /// links, so write it against the overload that carries them.
    /// </remarks>
    ValueTask<UserMutationDecision> OnBeforeLinkExternalLoginAsync(
        Guid userId, string provider, string subject, CancellationToken cancellationToken)
        => ValueTask.FromResult(UserMutationDecision.Allow());

    /// <summary>
    /// Called before an external identity is linked to the user, with the links the user already holds.
    /// This is the overload the module calls; override it for any rule over the user's links.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="provider">The provider key, lower-cased.</param>
    /// <param name="subject">The provider subject.</param>
    /// <param name="currentLogins">
    /// Every link the user holds in the ambient partition before this one, oldest first — what
    /// <see cref="IExternalLoginLinkService.GetLoginsAsync"/> would return. Passed in because a hook cannot
    /// ask that service itself: the service is the one calling the hook, so injecting it is a DI cycle.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    /// <remarks>
    /// The default forwards to the overload without <paramref name="currentLogins"/>, so an implementation
    /// written against that one is still consulted.
    /// <para>
    /// <paramref name="currentLogins"/> is read before the write, not locked: two concurrent links for one
    /// user each see the other's absent. A rule that must hold under concurrency needs a constraint of its own.
    /// </para>
    /// </remarks>
    ValueTask<UserMutationDecision> OnBeforeLinkExternalLoginAsync(
        Guid userId, string provider, string subject, IReadOnlyList<ExternalLoginInfo> currentLogins,
        CancellationToken cancellationToken = default)
        => OnBeforeLinkExternalLoginAsync(userId, provider, subject, cancellationToken);

    /// <summary>Called before every link the user holds for a provider is removed.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="provider">The provider key, lower-cased.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    /// <remarks>
    /// Unlinking is how a user without a password locks themselves out. The module cannot refuse the
    /// "last" sign-in method itself, because a method can live entirely outside it — a phone-OTP login
    /// built on <c>Themia.Challenges</c>, for one — so "last" would be wrong for somebody. The consumer
    /// knows every way its users sign in; that rule goes in the overload that carries the user's links.
    /// </remarks>
    ValueTask<UserMutationDecision> OnBeforeUnlinkExternalLoginAsync(
        Guid userId, string provider, CancellationToken cancellationToken)
        => ValueTask.FromResult(UserMutationDecision.Allow());

    /// <summary>
    /// Called before every link the user holds for a provider is removed, with all the links the user holds.
    /// This is the overload the module calls; override it for any rule over the user's links.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="provider">The provider key, lower-cased.</param>
    /// <param name="currentLogins">
    /// Every link the user holds in the ambient partition — for every provider, not only
    /// <paramref name="provider"/>, oldest first. "Never unlink the last channel" is a question about the
    /// others. Passed in for the same DI-cycle reason as on the link overload.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether to proceed.</returns>
    /// <remarks>
    /// The default forwards to the overload without <paramref name="currentLogins"/>, so an implementation
    /// written against that one is still consulted. Read before the write, not locked, as on the link overload.
    /// </remarks>
    ValueTask<UserMutationDecision> OnBeforeUnlinkExternalLoginAsync(
        Guid userId, string provider, IReadOnlyList<ExternalLoginInfo> currentLogins,
        CancellationToken cancellationToken = default)
        => OnBeforeUnlinkExternalLoginAsync(userId, provider, cancellationToken);

    /// <summary>Called after a mutation has been applied and saved.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="mutation">What changed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// Observation only — the change is already committed. Throwing from here does not undo it, so an
    /// implementation that cannot complete its work should record that rather than throw.
    /// </remarks>
    ValueTask OnUserMutatedAsync(
        Guid userId, UserMutation mutation, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
