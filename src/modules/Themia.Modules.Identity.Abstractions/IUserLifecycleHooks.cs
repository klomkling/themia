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
