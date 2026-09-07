using System.Net;
using Microsoft.AspNetCore.Http;
using Themia.Audit;
using Themia.Audit.Http;
using Themia.Audit.Redaction;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Xunit;

namespace Themia.Modules.Audit.Tests;

public class AuditingIdentityObserverTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string CallerIp = "203.0.113.7";
    private const string CallerUserAgent = "ThemiaAuditTests/1.0";

    // A real AuditRecorder over a fake store, not FakeAuditRecorder: the payload-in-Data assertions
    // below need the real serialize-then-redact path, not just the raw payload object.
    private FakeAuditStore Store { get; } = new();

    private AuditingIdentityObserver Observer { get; }

    public AuditingIdentityObserverTests()
    {
        var recorder = new AuditRecorder(
            Store,
            new AuditRedactor(new AuditRedactionOptions()),
            TimeProvider.System,
            new FakeAuditDialect(),
            new AuditOptions { ConnectionString = "fake", Engine = AuditEngine.Postgres });

        // A real HttpContext with a remote address and User-Agent header — not the parameterless
        // HttpContextAccessor, whose HttpContext is always null and makes AuditHttpEnricher.Enrich a
        // no-op, leaving IpAddress/UserAgent unasserted by every test in this file.
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(CallerIp);
        httpContext.Request.Headers.UserAgent = CallerUserAgent;
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        Observer = new AuditingIdentityObserver(recorder, new AuditHttpEnricher(accessor));
    }

    [Fact]
    public async Task Login_succeeded_records_the_user()
    {
        await Observer.OnLoginSucceededAsync(UserId, "alice", default);

        var entry = Store.LastWritten!;
        Assert.Equal("LOGIN_SUCCEEDED", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(UserId.ToString(), entry.ActorId);
        Assert.Null(entry.Reason);
    }

    [Fact]
    public async Task Login_failed_records_the_callers_ip_address_and_user_agent()
    {
        await Observer.OnLoginFailedAsync("someone", LoginFailureReason.WrongPassword, default);

        var entry = Store.LastWritten!;
        Assert.Equal(CallerIp, entry.IpAddress);
        Assert.Equal(CallerUserAgent, entry.UserAgent);
    }

    [Fact]
    public async Task Login_failure_records_the_internal_reason_that_the_client_never_sees()
    {
        await Observer.OnLoginFailedAsync("someone", LoginFailureReason.WrongPassword, default);

        var entry = Store.LastWritten!;
        Assert.Equal("LOGIN_FAILED", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Failure, entry.Outcome);
        Assert.Null(entry.ActorId);
        Assert.Equal(nameof(LoginFailureReason.WrongPassword), entry.Reason);
    }

    [Fact]
    public async Task Login_denied_records_the_hooks_reason()
    {
        await Observer.OnLoginDeniedAsync("someone", "blocked-country", default);

        var entry = Store.LastWritten!;
        Assert.Equal("LOGIN_DENIED", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Null(entry.ActorId);
        Assert.Equal("blocked-country", entry.Reason);
    }

    [Fact]
    public async Task External_login_records_account_creation_and_linking()
    {
        await Observer.OnExternalLoginSucceededAsync(UserId, "google", wasCreated: true, wasLinked: false, default);

        var entry = Store.LastWritten!;
        Assert.Equal("EXTERNAL_LOGIN_SUCCEEDED", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(UserId.ToString(), entry.ActorId);
        Assert.NotNull(entry.Data);
        Assert.Contains("\"wasCreated\":true", entry.Data!, StringComparison.Ordinal);
        Assert.Contains("\"wasLinked\":false", entry.Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_login_failure_records_the_provider_and_outcome()
    {
        await Observer.OnExternalLoginFailedAsync("google", ExternalLoginOutcome.ProviderRejected, default);

        var entry = Store.LastWritten!;
        Assert.Equal("EXTERNAL_LOGIN_FAILED", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Failure, entry.Outcome);
        Assert.Null(entry.ActorId);
        Assert.Equal("Provider", entry.EntityType);
        Assert.Equal("google", entry.EntityId);
        Assert.Equal(nameof(ExternalLoginOutcome.ProviderRejected), entry.Reason);
    }

    [Fact]
    public async Task External_login_denied_records_the_hooks_reason()
    {
        await Observer.OnExternalLoginDeniedAsync("line", "risk-score", default);

        var entry = Store.LastWritten!;
        Assert.Equal("EXTERNAL_LOGIN_DENIED", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Equal("line", entry.EntityId);
        Assert.Equal("risk-score", entry.Reason);
    }

    // ---- Untrusted-input clipping: an over-long CLIENT-SUPPLIED identifier must be clipped, not
    // rejected. AuditEntry.Validate() rejects an over-length adopter-named field outright, which is
    // correct for a value application code supplies — but userName/provider here are unbounded,
    // attacker-controlled strings, and letting Validate() throw would make AuthenticationFlow.RaiseAsync's
    // catch-all swallow the exception, silently deleting the audit row. Padding a brute-force attempt past
    // the column width must not let an attacker turn off their own audit trail. ----

    [Fact]
    public async Task Login_failed_with_an_over_long_username_is_clipped_not_rejected()
    {
        var overLong = new string('a', AuditEntry.MaxActorNameLength + 50);

        await Observer.OnLoginFailedAsync(overLong, LoginFailureReason.WrongPassword, default);

        var entry = Store.LastWritten!;
        Assert.NotNull(entry.ActorName);
        Assert.True(entry.ActorName!.Length <= AuditEntry.MaxActorNameLength);
        Assert.EndsWith("…[truncated]", entry.ActorName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_denied_with_an_over_long_username_is_clipped_not_rejected()
    {
        var overLong = new string('b', AuditEntry.MaxActorNameLength + 50);

        await Observer.OnLoginDeniedAsync(overLong, "blocked-country", default);

        var entry = Store.LastWritten!;
        Assert.NotNull(entry.ActorName);
        Assert.True(entry.ActorName!.Length <= AuditEntry.MaxActorNameLength);
        Assert.EndsWith("…[truncated]", entry.ActorName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_login_failed_with_an_over_long_provider_is_clipped_not_rejected()
    {
        var overLong = new string('c', AuditEntry.MaxEntityIdLength + 50);

        await Observer.OnExternalLoginFailedAsync(overLong, ExternalLoginOutcome.ProviderRejected, default);

        var entry = Store.LastWritten!;
        Assert.NotNull(entry.EntityId);
        Assert.True(entry.EntityId!.Length <= AuditEntry.MaxEntityIdLength);
        Assert.EndsWith("…[truncated]", entry.EntityId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_login_denied_with_an_over_long_provider_is_clipped_not_rejected()
    {
        var overLong = new string('d', AuditEntry.MaxEntityIdLength + 50);

        await Observer.OnExternalLoginDeniedAsync(overLong, "risk-score", default);

        var entry = Store.LastWritten!;
        Assert.NotNull(entry.EntityId);
        Assert.True(entry.EntityId!.Length <= AuditEntry.MaxEntityIdLength);
        Assert.EndsWith("…[truncated]", entry.EntityId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_succeeded_records_the_user()
    {
        await Observer.OnRefreshSucceededAsync(UserId, default);

        var entry = Store.LastWritten!;
        Assert.Equal("REFRESH_SUCCEEDED", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(UserId.ToString(), entry.ActorId);
    }

    [Fact]
    public async Task Refresh_denied_before_rotation_records_rotation_not_committed()
    {
        await Observer.OnRefreshDeniedAsync("someone", "device-mismatch", rotationCommitted: false, default);

        var entry = Store.LastWritten!;
        Assert.Equal("REFRESH_DENIED", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Equal("device-mismatch", entry.Reason);
        Assert.NotNull(entry.Data);
        Assert.Contains("\"rotationCommitted\":false", entry.Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_denied_after_rotation_records_rotation_committed()
    {
        await Observer.OnRefreshDeniedAsync(null, "revoked", rotationCommitted: true, default);

        var entry = Store.LastWritten!;
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Contains("\"rotationCommitted\":true", entry.Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_token_reuse_is_recorded_as_a_failure_with_its_outcome_and_the_owning_user()
    {
        await Observer.OnRefreshFailedAsync(UserId, RefreshOutcome.ReuseDetected, default);

        var entry = Store.LastWritten!;
        Assert.Equal("REFRESH_FAILED", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Failure, entry.Outcome);
        Assert.Equal(UserId.ToString(), entry.ActorId);
        Assert.Equal(nameof(RefreshOutcome.ReuseDetected), entry.Reason);
    }

    [Fact]
    public async Task Refresh_invalid_token_has_no_attributable_user()
    {
        await Observer.OnRefreshFailedAsync(null, RefreshOutcome.Invalid, default);

        var entry = Store.LastWritten!;
        Assert.Equal(AuditOutcome.Failure, entry.Outcome);
        Assert.Null(entry.ActorId);
        Assert.Equal(nameof(RefreshOutcome.Invalid), entry.Reason);
    }

    [Fact]
    public async Task Logout_records_the_user_and_whether_every_session_was_revoked()
    {
        await Observer.OnLogoutAsync(UserId, allSessions: true, default);

        var entry = Store.LastWritten!;
        Assert.Equal("LOGOUT", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(UserId.ToString(), entry.ActorId);
        Assert.Contains("\"allSessions\":true", entry.Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_with_unresolved_token_has_no_actor()
    {
        await Observer.OnLogoutAsync(null, allSessions: false, default);

        var entry = Store.LastWritten!;
        Assert.Null(entry.ActorId);
    }

    [Fact]
    public async Task Locked_out_records_the_user_and_lockout_end()
    {
        var lockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);

        await Observer.OnLockedOutAsync(UserId, lockoutEnd, default);

        var entry = Store.LastWritten!;
        Assert.Equal("LOCKED_OUT", entry.EventType);
        Assert.Equal(AuditCategory.Authentication, entry.Category);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(UserId.ToString(), entry.ActorId);
    }

    [Fact]
    public async Task User_mutated_uses_the_user_lifecycle_category()
    {
        await Observer.OnUserMutatedAsync(UserId, UserMutation.Password | UserMutation.Email, default);

        var entry = Store.LastWritten!;
        Assert.Equal("USER_MUTATED", entry.EventType);
        Assert.Equal(AuditCategory.UserLifecycle, entry.Category);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(UserId.ToString(), entry.ActorId);
        Assert.Contains("Password", entry.Data!, StringComparison.Ordinal);
        Assert.Contains("Email", entry.Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_mutation_refused_records_the_reason_and_the_user_lifecycle_category()
    {
        await Observer.OnUserMutationRefusedAsync(UserId, UserMutation.Deleted, "last admin", default);

        var entry = Store.LastWritten!;
        Assert.Equal("USER_MUTATION_REFUSED", entry.EventType);
        Assert.Equal(AuditCategory.UserLifecycle, entry.Category);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Equal(UserId.ToString(), entry.ActorId);
        Assert.Equal("last admin", entry.Reason);
        Assert.Contains("Deleted", entry.Data!, StringComparison.Ordinal);
    }
}
