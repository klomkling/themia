using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class ProofAuthTokenIssuerTests : IDisposable
{
    private const string Channel = "federation";
    private static readonly DateTimeOffset VerifiedAt = DateTimeOffset.Parse("2026-11-01T08:30:00Z");

    private readonly TenantId? originalTenant = TenantContextAccessor.CurrentTenantId;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-09T10:00:00Z"));
    private readonly FakeUserService users = new();
    private readonly FakeRefreshTokenService refresh = new();
    private readonly FakeAccessTokenService access;
    private readonly InMemorySessionProofStore store = new();
    private readonly ScriptedSecondFactorPolicy policy = new() { Requirement = SecondFactorRequirement.Verify };
    private readonly SecondFactorTicketService ticketService;
    private readonly User user = new() { UserName = "m", IsActive = true, TenantId = TenantId.From("t1") };

    public ProofAuthTokenIssuerTests()
    {
        access = new FakeAccessTokenService(clock);
        users.UserToReturn = user;
        ticketService = new SecondFactorTicketService(
            new EphemeralDataProtectionProvider(),
            new ServiceCollection().AddSingleton<IUserTokenService>(new InMemoryUserTokenService(clock)).BuildServiceProvider(),
            clock);
    }

    public void Dispose() => TenantContextAccessor.CurrentTenantId = originalTenant;

    private static SessionProofSnapshot Proof(DateTimeOffset? at = null) =>
        new("otp mfa", SessionProofStrength.Strong, at ?? VerifiedAt);

    private static SessionRequest Request(SessionProofSnapshot? proof = null) =>
        new(TenantId.From("t1"), Channel, null, proof);

    private GuardedAuthTokenIssuer Issuer(ISessionProofStore? proofs) =>
        new(users, new FakeClaimsPrincipalFactory(), access, refresh, policy, [new RecordingObserver()],
            new IdentityModuleOptions(), clock, NullLogger<GuardedAuthTokenIssuer>.Instance, ticketService, proofs);

    private async Task<PendingSecondFactor> PendingAsync() =>
        (await Issuer(store).StartSessionAsync(user.Id, Request())).SecondFactor!.Value;

    [Fact]
    public async Task Complete_with_a_proof_stores_it_on_the_new_session_and_puts_amr_and_auth_time_in_the_token()
    {
        var pending = await PendingAsync();

        var result = await ((IProofAuthTokenIssuer)Issuer(store)).CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request(), Proof());

        Assert.True(result.Succeeded);
        Assert.Equal(Proof(), store.Rows[(user.Id, refresh.LastFamilyId)]);
        Assert.Equal(["otp", "mfa"], access.LastPrincipal!.FindAll("amr").Select(c => c.Value));
        Assert.Equal(VerifiedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), access.LastPrincipal.FindFirst("auth_time")!.Value);
    }

    [Fact]
    public async Task Renew_after_a_proof_completion_keeps_amr_and_auth_time()
    {
        var pending = await PendingAsync();
        var issuer = (IProofAuthTokenIssuer)Issuer(store);
        var completed = await issuer.CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request(), Proof());
        refresh.LiveSessions.Add((user.Id, completed.SessionId!.Value));
        clock.Advance(TimeSpan.FromHours(6));

        await issuer.RenewSessionAsync(user.Id, completed.SessionId.Value, Request());

        Assert.Equal(["otp", "mfa"], access.LastPrincipal!.FindAll("amr").Select(c => c.Value));
        Assert.Equal(VerifiedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), access.LastPrincipal.FindFirst("auth_time")!.Value);
    }

    [Fact]
    public async Task Complete_with_a_proof_but_no_store_throws_before_the_ticket_is_spent_so_it_stays_retryable()
    {
        var pending = await PendingAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IProofAuthTokenIssuer)Issuer(null)).CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request(), Proof()));
        var retry = await ((IProofAuthTokenIssuer)Issuer(store)).CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request(), Proof());

        Assert.True(retry.Succeeded);
        Assert.Equal(1, refresh.IssueCalls);
    }

    [Fact]
    public async Task Complete_whose_proof_cannot_be_stored_throws_and_leaves_no_live_refresh_token()
    {
        var pending = await PendingAsync();
        store.SaveResult = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IProofAuthTokenIssuer)Issuer(store)).CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request(), Proof()));

        Assert.Empty(store.Rows);
        Assert.Null(access.LastPrincipal);
        Assert.True(refresh.RevokeCalls >= 1);
    }

    [Fact]
    public async Task Complete_refuses_a_request_that_already_carries_a_proof_before_the_ticket_is_spent()
    {
        var pending = await PendingAsync();
        var issuer = (IProofAuthTokenIssuer)Issuer(store);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            issuer.CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request(Proof()), Proof()));

        Assert.True((await issuer.CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request(), Proof())).Succeeded);
    }

    [Fact]
    public async Task Complete_refuses_a_proof_dated_in_the_future_and_writes_no_proof_row()
    {
        var pending = await PendingAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ((IProofAuthTokenIssuer)Issuer(store)).CompleteSessionAsync(
                user.Id, pending.Ticket, pending.Stage, Request(), Proof(clock.GetUtcNow().AddHours(1))));

        Assert.Empty(store.Rows);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Complete_refusals_write_no_proof_row()
    {
        var pending = await PendingAsync();
        var issuer = (IProofAuthTokenIssuer)Issuer(store);

        var wrongUser = await issuer.CompleteSessionAsync(Guid.NewGuid(), pending.Ticket, pending.Stage, Request(), Proof());

        Assert.Equal(IssueOutcome.TicketInvalid, wrongUser.Outcome);
        Assert.Empty(store.Rows);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Complete_refuses_a_user_who_was_locked_after_the_ticket_and_writes_no_proof_row()
    {
        var pending = await PendingAsync();
        user.LockoutEnd = clock.GetUtcNow().AddHours(1);

        var result = await ((IProofAuthTokenIssuer)Issuer(store)).CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request(), Proof());

        Assert.Equal(IssueOutcome.LockedOut, result.Outcome);
        Assert.Empty(store.Rows);
    }
}
