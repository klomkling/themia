using Themia.Modules.Identity.Abstractions;

namespace Themia.Modules.Identity.Tests.Fakes;

/// <summary>
/// <see cref="IUserLifecycleHooks"/> that records what it was asked and can refuse any single mutation.
/// </summary>
/// <remarks>
/// Every hook is settable independently so a test can prove that refusing one path leaves the others
/// alone — the failure mode this seam exists to prevent is a guard that covers some paths and reads as
/// covering all of them.
/// </remarks>
internal sealed class RecordingUserLifecycleHooks : IUserLifecycleHooks
{
    public string? RefuseSetEmail { get; set; }
    public string? RefuseConfirmEmail { get; set; }
    public string? RefuseSetPhoneNumber { get; set; }
    public string? RefuseConfirmPhoneNumber { get; set; }
    public string? RefuseSetPassword { get; set; }
    public string? RefuseSetActive { get; set; }
    public string? RefuseDelete { get; set; }

    /// <summary>The mutations announced through <see cref="IUserLifecycleHooks.OnUserMutatedAsync"/>.</summary>
    public List<UserMutation> Observed { get; } = [];

    /// <summary>The before-hooks that were called, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>The last email proposed to <see cref="IUserLifecycleHooks.OnBeforeSetEmailAsync"/>.</summary>
    public string? LastProposedEmail { get; private set; }

    /// <summary>The last number proposed to <see cref="IUserLifecycleHooks.OnBeforeSetPhoneNumberAsync"/>.</summary>
    public string? LastProposedPhoneNumber { get; private set; }

    /// <summary>The last state proposed to <see cref="IUserLifecycleHooks.OnBeforeSetActiveAsync"/>.</summary>
    public bool? LastProposedActive { get; private set; }

    public ValueTask<UserMutationDecision> OnBeforeSetEmailAsync(
        Guid userId, string? email, CancellationToken cancellationToken = default)
    {
        Asked.Add(nameof(OnBeforeSetEmailAsync));
        LastProposedEmail = email;
        return Decide(RefuseSetEmail);
    }

    public ValueTask<UserMutationDecision> OnBeforeConfirmEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        Asked.Add(nameof(OnBeforeConfirmEmailAsync));
        return Decide(RefuseConfirmEmail);
    }

    public ValueTask<UserMutationDecision> OnBeforeSetPhoneNumberAsync(
        Guid userId, string? phoneNumber, CancellationToken cancellationToken = default)
    {
        Asked.Add(nameof(OnBeforeSetPhoneNumberAsync));
        LastProposedPhoneNumber = phoneNumber;
        return Decide(RefuseSetPhoneNumber);
    }

    public ValueTask<UserMutationDecision> OnBeforeConfirmPhoneNumberAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        Asked.Add(nameof(OnBeforeConfirmPhoneNumberAsync));
        return Decide(RefuseConfirmPhoneNumber);
    }

    public ValueTask<UserMutationDecision> OnBeforeSetPasswordAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        Asked.Add(nameof(OnBeforeSetPasswordAsync));
        return Decide(RefuseSetPassword);
    }

    public ValueTask<UserMutationDecision> OnBeforeSetActiveAsync(
        Guid userId, bool isActive, CancellationToken cancellationToken = default)
    {
        Asked.Add(nameof(OnBeforeSetActiveAsync));
        LastProposedActive = isActive;
        return Decide(RefuseSetActive);
    }

    public ValueTask<UserMutationDecision> OnBeforeDeleteAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        Asked.Add(nameof(OnBeforeDeleteAsync));
        return Decide(RefuseDelete);
    }

    public ValueTask OnUserMutatedAsync(
        Guid userId, UserMutation mutation, CancellationToken cancellationToken = default)
    {
        Observed.Add(mutation);
        return ValueTask.CompletedTask;
    }

    private static ValueTask<UserMutationDecision> Decide(string? refusal) =>
        ValueTask.FromResult(refusal is null ? UserMutationDecision.Allow() : UserMutationDecision.Refuse(refusal));
}
