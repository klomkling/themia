using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Tests;

internal sealed class FakeUserService : IUserService
{
    public PasswordVerificationResult VerifyResult { get; set; } = PasswordVerificationResult.Success;
    public User? UserToReturn { get; set; }

    /// <summary>Returned by <see cref="FindByEmailAsync"/>. Null by default so an existing test that sets
    /// only <see cref="UserToReturn"/> resolves by username alone — the pre-multi-identifier behaviour.</summary>
    public User? EmailUserToReturn { get; set; }

    /// <summary>Returned by <see cref="FindByPhoneAsync"/>. Null by default, as above.</summary>
    public User? PhoneUserToReturn { get; set; }
    public int VerifyCalls { get; private set; }

    /// <summary>The user name the flow actually verified against — proves the lockout state machine is
    /// keyed on the account rather than on whichever identifier the caller happened to type.</summary>
    public string? VerifiedUserName { get; private set; }

    public int FindByUserNameCalls { get; private set; }

    public int FindByEmailCalls { get; private set; }

    public int FindByPhoneCalls { get; private set; }

    public Task<PasswordVerificationResult> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        VerifyCalls++;
        VerifiedUserName = userName;
        return Task.FromResult(VerifyResult);
    }

    public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        FindByUserNameCalls++;
        return Task.FromResult(UserToReturn);
    }

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(UserToReturn);

    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        FindByEmailCalls++;
        return Task.FromResult(EmailUserToReturn);
    }

    public Task<User?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        FindByPhoneCalls++;
        return Task.FromResult(PhoneUserToReturn);
    }
    public Task<UserMutationResult> SetEmailAsync(Guid userId, string? email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> ConfirmEmailAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> SetPhoneNumberAsync(Guid userId, string? phoneNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> ConfirmPhoneNumberAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserCreationResult> CreateAsync(string userName, string password, string? email = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserCreationResult> CreateExternalUserAsync(string userName, string? email, bool emailVerified, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeClaimsPrincipalFactory : IClaimsPrincipalFactory
{
    public Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], authenticationType)));
}

internal sealed class FakeAccessTokenService(TimeProvider? clock = null) : IAccessTokenService
{
    public int IssueCalls { get; private set; }

    public AccessToken Issue(ClaimsPrincipal principal)
    {
        IssueCalls++;
        return new("access-jwt", (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(15));
    }
}

internal sealed class FakeRefreshTokenService : IRefreshTokenService
{
    public int IssueCalls { get; private set; }
    public RefreshValidationResult RotateResult { get; set; }
    public int RevokeCalls { get; private set; }
    public bool LastRevokeAllForUser { get; private set; }

    /// <summary>Returned by <see cref="ResolveOwnerAsync"/>. Null by default, matching the interface's
    /// default (an unresolvable token).</summary>
    public Guid? OwnerId { get; set; }

    public Task<RefreshIssue> IssueAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        IssueCalls++;
        return Task.FromResult(new RefreshIssue("refresh-raw", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid()));
    }

    public Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default) =>
        Task.FromResult(RotateResult);

    public Task<Guid?> ResolveOwnerAsync(string rawToken, CancellationToken cancellationToken = default) =>
        Task.FromResult(OwnerId);

    public Task RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default)
    {
        RevokeCalls++;
        LastRevokeAllForUser = allForUser;
        return Task.CompletedTask;
    }
}

internal sealed class FakePasswordHasher : IPasswordHasher
{
    public int HashCalls { get; private set; }
    public string Hash(string password) { HashCalls++; return "hash"; }
    public bool Verify(string encodedHash, string password) => true;
    public bool NeedsRehash(string encodedHash) => false;
}

internal sealed class RecordingHooks : Themia.Modules.Identity.AspNetCore.Authentication.AuthenticationHooksBase
{
    public bool DenyBeforeLogin { get; set; }
    public bool DenyOnSucceeded { get; set; }
    public bool DenyBeforeRefresh { get; set; }
    public List<string> Calls { get; } = [];
    public LoginFailureReason? FailedReason { get; private set; }
    public bool SucceededRanBeforeIssue { get; set; }
    public FakeRefreshTokenService? Refresh { get; set; }

    public override Task OnBeforeLoginAsync(BeforeLoginContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("before-login");
        if (DenyBeforeLogin) context.Deny("blocked");
        return Task.CompletedTask;
    }

    public override Task OnLoginSucceededAsync(LoginSucceededContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("login-succeeded");
        if (Refresh is not null) SucceededRanBeforeIssue = Refresh.IssueCalls == 0;
        if (DenyOnSucceeded) context.Deny("gated");
        return Task.CompletedTask;
    }

    public override Task OnLoginFailedAsync(LoginFailedContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("login-failed");
        FailedReason = context.Reason;
        return Task.CompletedTask;
    }

    public override Task OnBeforeRefreshAsync(BeforeRefreshContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("before-refresh");
        if (DenyBeforeRefresh) context.Deny();
        return Task.CompletedTask;
    }

    public bool DenyRefreshSucceeded { get; set; }

    public override Task OnRefreshSucceededAsync(RefreshSucceededContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("refresh-succeeded");
        if (DenyRefreshSucceeded) context.Deny("blocked-after-refresh");
        return Task.CompletedTask;
    }
}

/// <summary>Records every <see cref="IIdentityEventObserver"/> invocation by method name, plus the last
/// arguments seen for each — enough for the coverage theory (which event fired) and the targeted
/// assertions (rotationCommitted, wasCreated/wasLinked, userId).</summary>
internal sealed class RecordingObserver : IIdentityEventObserver
{
    public List<string> Calls { get; } = [];

    public (string? UserName, string? DenialReason, bool RotationCommitted)? LastRefreshDenied { get; private set; }
    public (Guid? UserId, RefreshOutcome Outcome)? LastRefreshFailed { get; private set; }
    public (Guid? UserId, bool AllSessions)? LastLogout { get; private set; }
    public (Guid UserId, UserMutation Mutation)? LastUserMutated { get; private set; }
    public (Guid UserId, DateTimeOffset LockoutEnd)? LastLockedOut { get; private set; }

    public Task OnLoginSucceededAsync(Guid userId, string userName, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnLoginSucceededAsync));
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string userName, LoginFailureReason reason, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnLoginFailedAsync));
        return Task.CompletedTask;
    }

    public Task OnLoginDeniedAsync(string userName, string? denialReason, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnLoginDeniedAsync));
        return Task.CompletedTask;
    }

    public Task OnRefreshSucceededAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnRefreshSucceededAsync));
        return Task.CompletedTask;
    }

    public Task OnRefreshDeniedAsync(string? userName, string? denialReason, bool rotationCommitted, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnRefreshDeniedAsync));
        LastRefreshDenied = (userName, denialReason, rotationCommitted);
        return Task.CompletedTask;
    }

    public Task OnRefreshFailedAsync(Guid? userId, RefreshOutcome outcome, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnRefreshFailedAsync));
        LastRefreshFailed = (userId, outcome);
        return Task.CompletedTask;
    }

    public Task OnLogoutAsync(Guid? userId, bool allSessions, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnLogoutAsync));
        LastLogout = (userId, allSessions);
        return Task.CompletedTask;
    }

    public Task OnLockedOutAsync(Guid userId, DateTimeOffset lockoutEnd, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnLockedOutAsync));
        LastLockedOut = (userId, lockoutEnd);
        return Task.CompletedTask;
    }

    public Task OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnUserMutatedAsync));
        LastUserMutated = (userId, mutation);
        return Task.CompletedTask;
    }
}

/// <summary>An observer where every method throws, to prove a throwing observer cannot change the flow it
/// observes.</summary>
internal sealed class ThrowingObserver : IIdentityEventObserver
{
    private static Task Throw() => throw new InvalidOperationException("observer failure");

    public Task OnLoginSucceededAsync(Guid userId, string userName, CancellationToken cancellationToken = default) => Throw();
    public Task OnLoginFailedAsync(string userName, LoginFailureReason reason, CancellationToken cancellationToken = default) => Throw();
    public Task OnLoginDeniedAsync(string userName, string? denialReason, CancellationToken cancellationToken = default) => Throw();
    public Task OnRefreshSucceededAsync(Guid userId, CancellationToken cancellationToken = default) => Throw();
    public Task OnRefreshDeniedAsync(string? userName, string? denialReason, bool rotationCommitted, CancellationToken cancellationToken = default) => Throw();
    public Task OnRefreshFailedAsync(Guid? userId, RefreshOutcome outcome, CancellationToken cancellationToken = default) => Throw();
    public Task OnLogoutAsync(Guid? userId, bool allSessions, CancellationToken cancellationToken = default) => Throw();
    public Task OnLockedOutAsync(Guid userId, DateTimeOffset lockoutEnd, CancellationToken cancellationToken = default) => Throw();
    public Task OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default) => Throw();
}
