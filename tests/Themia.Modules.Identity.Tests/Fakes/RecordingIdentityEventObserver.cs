using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.Tests.Fakes;

/// <summary>Records every <see cref="IIdentityEventObserver"/> invocation by method name, plus the last
/// arguments seen for the two events this project's <see cref="UserService"/>-scoped tests exercise:
/// lockout and user mutation.</summary>
internal sealed class RecordingIdentityEventObserver : IIdentityEventObserver
{
    public List<string> Calls { get; } = [];

    public (Guid UserId, DateTimeOffset LockoutEnd)? LastLockedOut { get; private set; }

    public List<(Guid UserId, UserMutation Mutation)> Mutations { get; } = [];

    public List<(Guid UserId, UserMutation Mutation, string Reason)> Refusals { get; } = [];

    public Task OnLockedOutAsync(Guid userId, DateTimeOffset lockoutEnd, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnLockedOutAsync));
        LastLockedOut = (userId, lockoutEnd);
        return Task.CompletedTask;
    }

    public Task OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnUserMutatedAsync));
        Mutations.Add((userId, mutation));
        return Task.CompletedTask;
    }

    public Task OnUserMutationRefusedAsync(
        Guid userId, UserMutation mutation, string reason, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnUserMutationRefusedAsync));
        Refusals.Add((userId, mutation, reason));
        return Task.CompletedTask;
    }
}

/// <summary>An observer where every method throws, to prove a throwing observer cannot change the result
/// of the operation it observes.</summary>
internal sealed class ThrowingIdentityEventObserver : IIdentityEventObserver
{
    /// <summary>When set, every method throws <see cref="OperationCanceledException"/> instead of
    /// <see cref="InvalidOperationException"/> — proving the fan-out swallows a cancelled observer write
    /// exactly like any other observer failure, rather than letting it fault an already-decided outcome.</summary>
    public bool ThrowOperationCanceled { get; set; }

    private Task Throw()
    {
        if (ThrowOperationCanceled)
        {
            throw new OperationCanceledException("observer cancelled");
        }

        throw new InvalidOperationException("observer failure");
    }

    public Task OnLockedOutAsync(Guid userId, DateTimeOffset lockoutEnd, CancellationToken cancellationToken = default) => Throw();
    public Task OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default) => Throw();
    public Task OnUserMutationRefusedAsync(Guid userId, UserMutation mutation, string reason, CancellationToken cancellationToken = default) => Throw();
}
