using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Tests;

internal sealed class FakeUserService : IUserService
{
    public PasswordVerificationResult VerifyResult { get; set; } = PasswordVerificationResult.Success;
    public User? UserToReturn { get; set; }
    public int VerifyCalls { get; private set; }

    public Task<PasswordVerificationResult> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        VerifyCalls++;
        return Task.FromResult(VerifyResult);
    }

    public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default) => Task.FromResult(UserToReturn);
    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(UserToReturn);
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(UserToReturn);
    public Task<UserCreationResult> CreateAsync(string userName, string password, string? email = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeClaimsPrincipalFactory : IClaimsPrincipalFactory
{
    public Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], authenticationType)));
}

internal sealed class FakeAccessTokenService(TimeProvider? clock = null) : IAccessTokenService
{
    public AccessToken Issue(ClaimsPrincipal principal) =>
        new("access-jwt", (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(15));
}

internal sealed class FakeRefreshTokenService : IRefreshTokenService
{
    public int IssueCalls { get; private set; }
    public RefreshValidationResult RotateResult { get; set; }
    public int RevokeCalls { get; private set; }
    public bool LastRevokeAllForUser { get; private set; }

    public Task<RefreshIssue> IssueAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        IssueCalls++;
        return Task.FromResult(new RefreshIssue("refresh-raw", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid()));
    }

    public Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default) =>
        Task.FromResult(RotateResult);

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
