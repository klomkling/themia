namespace Themia.Modules.Identity.Abstractions;

/// <summary>Why a mutation on <see cref="IUserService"/> succeeded or failed.</summary>
/// <remarks>
/// An enum rather than a <see cref="bool"/>: <see cref="Refused"/> and <see cref="UserNotFound"/> need
/// opposite handling — one is a rule the operator can be told about, the other is a programming error —
/// and a bool collapses them into a single false every call site is free to ignore. Adding a state later
/// then breaks an exhaustive <c>switch</c> instead of compiling silently at every existing caller.
/// </remarks>
public enum UserMutationOutcome
{
    /// <summary>The mutation was applied.</summary>
    Success,

    /// <summary>No user matched the id.</summary>
    UserNotFound,

    /// <summary>Another user already holds the value. Only reachable for email and phone number.</summary>
    Duplicate,

    /// <summary>An <see cref="IUserLifecycleHooks"/> implementation refused it. See the reason.</summary>
    Refused,
}

/// <summary>The outcome of a mutation on <see cref="IUserService"/>.</summary>
public readonly record struct UserMutationResult
{
    private UserMutationResult(UserMutationOutcome outcome, string? reason)
    {
        Outcome = outcome;
        Reason = reason;
    }

    /// <summary>Why it succeeded or failed.</summary>
    public UserMutationOutcome Outcome { get; }

    /// <summary>
    /// The refusal reason when <see cref="Outcome"/> is <see cref="UserMutationOutcome.Refused"/>;
    /// otherwise null. Written by the consumer's hook and intended to be shown to whoever attempted
    /// the change.
    /// </summary>
    public string? Reason { get; }

    /// <summary>Whether the mutation was applied.</summary>
    public bool Succeeded => Outcome == UserMutationOutcome.Success;

    /// <summary>The mutation was applied.</summary>
    public static UserMutationResult Success() => new(UserMutationOutcome.Success, null);

    /// <summary>No user matched the id.</summary>
    public static UserMutationResult UserNotFound() => new(UserMutationOutcome.UserNotFound, null);

    /// <summary>Another user already holds the value.</summary>
    public static UserMutationResult Duplicate() => new(UserMutationOutcome.Duplicate, null);

    /// <summary>A hook refused the change.</summary>
    /// <param name="reason">What to tell whoever attempted it.</param>
    public static UserMutationResult Refused(string reason) => new(UserMutationOutcome.Refused, reason);
}

/// <summary>A hook's answer to a proposed mutation.</summary>
public readonly record struct UserMutationDecision
{
    private UserMutationDecision(bool allowed, string? reason)
    {
        IsAllowed = allowed;
        Reason = reason;
    }

    /// <summary>Whether the mutation may proceed.</summary>
    public bool IsAllowed { get; }

    /// <summary>Why it was refused; null when allowed.</summary>
    public string? Reason { get; }

    /// <summary>Let the mutation proceed.</summary>
    public static UserMutationDecision Allow() => new(true, null);

    /// <summary>
    /// Refuse the mutation. Nothing is written and the caller receives
    /// <see cref="UserMutationOutcome.Refused"/> carrying <paramref name="reason"/>.
    /// </summary>
    /// <param name="reason">
    /// What to tell whoever attempted the change — "this is the only way you can sign in", not
    /// "refused". It reaches an operator, so write it for one.
    /// </param>
    public static UserMutationDecision Refuse(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new UserMutationDecision(false, reason);
    }
}

/// <summary>What changed about a user.</summary>
[Flags]
public enum UserMutation
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The password hash was replaced.</summary>
    Password = 1,

    /// <summary>The email address was set or cleared.</summary>
    Email = 2,

    /// <summary>The email was confirmed.</summary>
    EmailConfirmation = 4,

    /// <summary>The phone number was set or cleared.</summary>
    Phone = 8,

    /// <summary>The phone number was confirmed.</summary>
    PhoneConfirmation = 16,

    /// <summary>The account was activated or deactivated.</summary>
    Active = 32,

    /// <summary>The user was deleted.</summary>
    Deleted = 64,
}
