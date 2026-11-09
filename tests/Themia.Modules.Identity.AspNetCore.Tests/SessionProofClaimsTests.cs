using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class SessionProofClaimsTests : IDisposable
{
    private static readonly DateTimeOffset VerifiedAt = DateTimeOffset.Parse("2026-11-01T08:30:00Z");

    private readonly TenantId? originalTenant = TenantContextAccessor.CurrentTenantId;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-09T10:00:00Z"));
    private readonly FakeUserService users = new();
    private readonly FakeRefreshTokenService refresh = new();
    private readonly FakeAccessTokenService access;
    private readonly InMemorySessionProofStore store = new();
    private readonly User user = new() { UserName = "m", IsActive = true, TenantId = TenantId.From("t1") };

    public SessionProofClaimsTests()
    {
        access = new FakeAccessTokenService(clock);
        users.UserToReturn = user;
    }

    public void Dispose() => TenantContextAccessor.CurrentTenantId = originalTenant;

    private static SessionProofSnapshot Proof() =>
        new("otp mfa", SessionProofStrength.Strong, VerifiedAt);

    private GuardedAuthTokenIssuer Issuer(ISessionProofStore? proofs) =>
        new(users, new FakeClaimsPrincipalFactory(), access, refresh, new ScriptedSecondFactorPolicy(), [new RecordingObserver()],
            new IdentityModuleOptions(), clock, NullLogger<GuardedAuthTokenIssuer>.Instance, null, proofs);

    private AuthenticationFlow Flow() =>
        new(users, new FakeClaimsPrincipalFactory(), access, refresh, new FakePasswordHasher(), new RecordingHooks(), [],
            new IdentityModuleOptions(), clock, NullLogger<AuthenticationFlow>.Instance, NotRequiredSecondFactorPolicy.Instance, null, store);

    private static SessionRequest Request(SessionProofSnapshot? proof = null, IReadOnlyCollection<Claim>? claims = null) =>
        new(TenantId.From("t1"), "federation", claims, proof);

    private static void AssertProofClaims(ClaimsPrincipal principal)
    {
        Assert.Equal(["otp", "mfa"], principal.FindAll("amr").Select(c => c.Value));
        Assert.Equal(VerifiedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), principal.FindFirst("auth_time")!.Value);
    }

    private RefreshValidationResult Rotated(Guid family, bool replay)
    {
        var successor = new RefreshIssue("new-refresh", clock.GetUtcNow().AddDays(14), family);
        return replay ? RefreshValidationResult.ReplayedWithinGrace(user, successor) : RefreshValidationResult.Success(user, successor);
    }

    [Fact]
    public async Task Start_stores_the_proof_under_the_new_session_and_puts_it_in_the_first_token()
    {
        var result = await Issuer(store).StartSessionAsync(user.Id, Request(Proof()));

        Assert.True(result.Succeeded);
        Assert.Equal(Proof(), store.Rows[(user.Id, refresh.LastFamilyId)]);
        AssertProofClaims(access.LastPrincipal!);
    }

    [Fact]
    public async Task Start_with_a_proof_but_no_store_issues_nothing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Issuer(null).StartSessionAsync(user.Id, Request(Proof())));

        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Start_without_a_proof_puts_no_amr_or_auth_time_in_the_token()
    {
        await Issuer(store).StartSessionAsync(user.Id, Request());

        Assert.Empty(access.LastPrincipal!.FindAll("amr"));
        Assert.Null(access.LastPrincipal.FindFirst("auth_time"));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Renew_carries_the_stored_amr_and_auth_time_unchanged()
    {
        var session = Guid.NewGuid();
        refresh.LiveSessions.Add((user.Id, session));
        store.Rows[(user.Id, session)] = Proof();
        clock.Advance(TimeSpan.FromHours(6));

        await Issuer(store).RenewSessionAsync(user.Id, session, Request());

        AssertProofClaims(access.LastPrincipal!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rotation_and_a_lost_race_replay_carry_the_stored_amr_and_auth_time_unchanged(bool replay)
    {
        var family = Guid.NewGuid();
        store.Rows[(user.Id, family)] = Proof();
        refresh.RotateResult = Rotated(family, replay);
        clock.Advance(TimeSpan.FromHours(6));

        var result = await Flow().RefreshAsync("token");

        Assert.True(result.Succeeded);
        AssertProofClaims(access.LastPrincipal!);
    }

    [Fact]
    public async Task Rotation_of_a_session_with_no_stored_proof_carries_no_second_factor()
    {
        refresh.RotateResult = Rotated(Guid.NewGuid(), replay: false);

        await Flow().RefreshAsync("token");

        Assert.Empty(access.LastPrincipal!.FindAll("amr"));
        Assert.Null(access.LastPrincipal.FindFirst("auth_time"));
    }

    [Theory]
    [InlineData("amr")]
    [InlineData("auth_time")]
    public async Task An_app_cannot_set_amr_or_auth_time_as_a_claim(string type)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Issuer(store).StartSessionAsync(user.Id, Request(claims: [new Claim(type, "x")])));
    }
}

internal sealed class InMemorySessionProofStore : ISessionProofStore
{
    public Dictionary<(Guid UserId, Guid SessionId), SessionProofSnapshot> Rows { get; } = [];

    /// <summary>What <see cref="SaveAsync"/> reports; false stores nothing, as for a session that is not live.</summary>
    public bool SaveResult { get; set; } = true;

    public Task<SessionProofSnapshot?> GetAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rows.GetValueOrDefault((userId, sessionId)));

    public Task<bool> SaveAsync(Guid userId, Guid sessionId, SessionProofSnapshot proof, CancellationToken cancellationToken = default)
    {
        if (SaveResult)
        {
            Rows[(userId, sessionId)] = proof;
        }

        return Task.FromResult(SaveResult);
    }

    public Task<bool> LiftRestrictionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
