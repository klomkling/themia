using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

/// <summary>A refresh session is bound to the stamp it was issued under: a credential event (password reset,
/// reactivation) ends it, a role or claim change rebinds it, a rehash at login leaves it alone.</summary>
public class RefreshStampBindingTests
{
    private static readonly TenantId Acme = new("acme");
    private readonly List<User> users = [];
    private readonly List<RefreshToken> tokens = [];
    private readonly List<Role> roles = [];
    private readonly List<UserRole> memberships = [];
    private readonly List<RoleClaim> roleClaims = [];
    private readonly List<UserClaim> userClaims = [];
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-31T10:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly FakeRepository<User> userRepo;
    private readonly FakeRepository<RefreshToken> tokenRepo;
    private readonly FakeUnitOfWork uow = new();

    public RefreshStampBindingTests()
    {
        userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Acme };
        tokenRepo = new FakeRepository<RefreshToken>(tokens, t => t.Id);
    }

    private RefreshTokenService Tokens() => new(userRepo, tokenRepo, uow, clock, options, NullLogger<RefreshTokenService>.Instance);

    private RoleService Roles() =>
        new(userRepo, new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = Acme },
            new FakeRepository<UserRole>(memberships, m => m.Id) { AmbientTenant = Acme }, uow, new DataFilterScope(),
            new MemoryCache(new MemoryCacheOptions()), tokenRepo);

    private ClaimService Claims() =>
        new(new FakeRepository<UserClaim>(userClaims, c => c.Id) { AmbientTenant = Acme },
            new FakeRepository<RoleClaim>(roleClaims, c => c.Id) { AmbientTenant = Acme },
            new FakeRepository<UserRole>(memberships, m => m.Id) { AmbientTenant = Acme }, userRepo,
            new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = Acme }, uow, new MemoryCache(new MemoryCacheOptions()), tokenRepo);

    private User AddUser(string name = "alice")
    {
        var u = new User { UserName = name, NormalizedUserName = name.ToUpperInvariant(), TenantId = Acme };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u;
    }

    private static void RotateStamp(User u) => u.SecurityStamp = SecurityStampCache.NewStamp();

    [Fact]
    public async Task Issue_binds_the_family_to_the_users_stamp_and_the_successor_inherits_it()
    {
        var user = AddUser();
        var first = await Tokens().IssueAsync(user.Id);

        Assert.True((await Tokens().ValidateAndRotateAsync(first.RawToken)).TryGetSuccess(out _, out _));

        Assert.All(tokens, t => Assert.Equal(user.SecurityStamp, t.SecurityStamp));
        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public async Task Refresh_after_a_credential_event_is_Invalid_and_revokes_the_family()
    {
        var user = AddUser();
        var issued = await Tokens().IssueAsync(user.Id);
        RotateStamp(user);   // password reset or reactivation

        var result = await Tokens().ValidateAndRotateAsync(issued.RawToken);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.Single(tokens);   // no successor minted
    }

    [Fact]
    public async Task A_row_issued_before_the_column_existed_is_accepted_once_and_bound_to_the_current_stamp()
    {
        var user = AddUser();
        var issued = await Tokens().IssueAsync(user.Id);
        tokens.Single().SecurityStamp = null;   // legacy row

        Assert.True((await Tokens().ValidateAndRotateAsync(issued.RawToken)).TryGetSuccess(out _, out var next));

        Assert.Equal(user.SecurityStamp, tokens.Single(t => t.ConsumedAt is null).SecurityStamp);
        RotateStamp(user);
        Assert.Equal(RefreshOutcome.Invalid, (await Tokens().ValidateAndRotateAsync(next.RawToken)).Outcome);
    }

    [Fact]
    public async Task IsSessionLive_is_false_once_the_stamp_rotated_and_true_for_an_unbound_legacy_row()
    {
        var user = AddUser();
        var issued = await Tokens().IssueAsync(user.Id);
        Assert.True(await Tokens().IsSessionLiveAsync(user.Id, issued.FamilyId));

        RotateStamp(user);
        Assert.False(await Tokens().IsSessionLiveAsync(user.Id, issued.FamilyId));

        tokens.Single().SecurityStamp = null;
        Assert.True(await Tokens().IsSessionLiveAsync(user.Id, issued.FamilyId));
    }

    [Fact]
    public async Task A_replay_within_the_grace_after_a_credential_event_revokes_without_the_theft_path_and_returns_no_successor()
    {
        options.RefreshReuseGrace = TimeSpan.FromSeconds(30);
        var user = AddUser();
        var issued = await Tokens().IssueAsync(user.Id);
        Assert.True((await Tokens().ValidateAndRotateAsync(issued.RawToken)).TryGetSuccess(out _, out _));
        RotateStamp(user);

        var replay = await Tokens().ValidateAndRotateAsync(issued.RawToken);

        Assert.Equal(RefreshOutcome.Invalid, replay.Outcome);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Revoke_all_except_rebinds_the_kept_session_so_it_survives_a_password_change()
    {
        var user = AddUser();
        var kept = await Tokens().IssueAsync(user.Id);
        var other = await Tokens().IssueAsync(user.Id);
        RotateStamp(user);   // SetPasswordAsync ran first

        await Tokens().RevokeAllForUserExceptAsync(user.Id, kept.FamilyId.ToString());

        Assert.True((await Tokens().ValidateAndRotateAsync(kept.RawToken)).TryGetSuccess(out _, out _));
        Assert.Equal(RefreshOutcome.ReuseDetected, (await Tokens().ValidateAndRotateAsync(other.RawToken)).Outcome);
    }

    [Fact]
    public async Task Revoke_all_except_before_the_rotation_ends_the_kept_session_at_its_next_refresh()
    {
        var user = AddUser();
        var kept = await Tokens().IssueAsync(user.Id);
        await Tokens().IssueAsync(user.Id);

        await Tokens().RevokeAllForUserExceptAsync(user.Id, kept.FamilyId.ToString());
        RotateStamp(user);   // wrong order: the mutation came second

        Assert.Equal(RefreshOutcome.Invalid, (await Tokens().ValidateAndRotateAsync(kept.RawToken)).Outcome);
    }

    [Fact]
    public async Task Adding_and_removing_a_role_keeps_the_session_alive()
    {
        var user = AddUser();
        var role = new Role { Name = "r", NormalizedName = "R", TenantId = Acme };
        role.SetId(Guid.NewGuid());
        roles.Add(role);
        var issued = await Tokens().IssueAsync(user.Id);

        var stampBefore = user.SecurityStamp;
        Assert.True(await Roles().AssignRoleAsync(user.Id, role.Id));
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        Assert.True((await Tokens().ValidateAndRotateAsync(issued.RawToken)).TryGetSuccess(out _, out var second));

        Assert.True(await Roles().RemoveRoleAsync(user.Id, role.Id));
        Assert.True((await Tokens().ValidateAndRotateAsync(second.RawToken)).TryGetSuccess(out _, out _));
    }

    [Fact]
    public async Task A_role_edit_does_not_revive_a_session_that_predates_a_password_reset()
    {
        var user = AddUser();
        var role = new Role { Name = "r", NormalizedName = "R", TenantId = Acme };
        role.SetId(Guid.NewGuid());
        roles.Add(role);
        var stale = await Tokens().IssueAsync(user.Id);   // e.g. a stolen session
        RotateStamp(user);                                  // password reset: stale is dead at its next refresh, not yet refused
        var current = await Tokens().IssueAsync(user.Id);   // the user signs in again afterwards

        await Roles().AssignRoleAsync(user.Id, role.Id);

        Assert.Equal(RefreshOutcome.Invalid, (await Tokens().ValidateAndRotateAsync(stale.RawToken)).Outcome);
        Assert.True((await Tokens().ValidateAndRotateAsync(current.RawToken)).TryGetSuccess(out _, out _));
    }

    [Fact]
    public async Task A_claim_edit_does_not_revive_a_stale_session_of_a_holder()
    {
        var holder = AddUser("holder");
        var role = new Role { Name = "r", NormalizedName = "R", TenantId = Acme };
        role.SetId(Guid.NewGuid());
        roles.Add(role);
        await Roles().AssignRoleAsync(holder.Id, role.Id);
        var stale = await Tokens().IssueAsync(holder.Id);
        RotateStamp(holder);   // password reset

        await Claims().AddRoleClaimAsync(role.Id, "perm", "x");

        Assert.Equal(RefreshOutcome.Invalid, (await Tokens().ValidateAndRotateAsync(stale.RawToken)).Outcome);
    }

    [Fact]
    public async Task A_rotation_that_lost_the_race_still_rotates_from_the_current_stamp_and_revives_nothing()
    {
        var user = AddUser();
        var stale = await Tokens().IssueAsync(user.Id);   // bound to S1
        var s1 = user.SecurityStamp;
        RotateStamp(user);                                  // a password reset commits S2 after the role change read S1
        var s2 = user.SecurityStamp;
        var afterReset = await Tokens().IssueAsync(user.Id); // the user signs in again: bound to S2, token carries S2
        var changed = SecurityStampCache.NewStamp();

        await RefreshStampBinding.RotateAndRebindAsync(
            userRepo, tokenRepo, new Dictionary<Guid, string> { [user.Id] = s1 }, changed, CancellationToken.None);

        // The change still cuts the token minted after the reset (it carries S2): the stamp moved on from S2, not stayed on it.
        Assert.Equal(changed, user.SecurityStamp);
        Assert.NotEqual(s2, user.SecurityStamp);
        // The session opened after the reset follows; the one the reset ended stays ended.
        Assert.True((await Tokens().ValidateAndRotateAsync(afterReset.RawToken)).TryGetSuccess(out _, out _));
        Assert.Equal(RefreshOutcome.Invalid, (await Tokens().ValidateAndRotateAsync(stale.RawToken)).Outcome);
    }

    [Fact]
    public async Task A_rotation_whose_swaps_run_out_still_moves_the_stamp_and_rebinds_nothing()
    {
        var user = AddUser();
        var stale = await Tokens().IssueAsync(user.Id);
        var s1 = user.SecurityStamp;
        RotateStamp(user);
        var afterReset = await Tokens().IssueAsync(user.Id);
        var changed = SecurityStampCache.NewStamp();

        // One attempt: the swap from the stale S1 misses and the loop ends, so the rotation is forced.
        await RefreshStampBinding.RotateAndRebindAsync(
            userRepo, tokenRepo, new Dictionary<Guid, string> { [user.Id] = s1 }, changed, CancellationToken.None, maxSwapAttempts: 1);

        Assert.Equal(changed, user.SecurityStamp);
        Assert.Equal(RefreshOutcome.Invalid, (await Tokens().ValidateAndRotateAsync(stale.RawToken)).Outcome);
        Assert.Equal(RefreshOutcome.Invalid, (await Tokens().ValidateAndRotateAsync(afterReset.RawToken)).Outcome);
    }

    [Fact]
    public async Task A_rotation_for_a_user_who_no_longer_exists_does_nothing_and_does_not_throw()
    {
        var user = AddUser();
        await Tokens().IssueAsync(user.Id);
        users.Clear();

        await RefreshStampBinding.RotateAndRebindAsync(
            userRepo, tokenRepo, new Dictionary<Guid, string> { [user.Id] = "gone" }, "new", CancellationToken.None);

        Assert.Empty(users);
    }

    [Fact]
    public async Task Rebind_writes_only_the_stamp_column_and_leaves_revoked_and_rotated_rows_as_they_are()
    {
        var user = AddUser();
        var issued = await Tokens().IssueAsync(user.Id);
        Assert.True((await Tokens().ValidateAndRotateAsync(issued.RawToken)).TryGetSuccess(out _, out var next));
        var other = await Tokens().IssueAsync(user.Id);
        await Tokens().RevokeAsync(other.RawToken, allForUser: false);
        var snapshot = tokens.Select(t => (t.Id, t.ConsumedAt, t.RevokedAt, t.ReplacedById, t.SecurityStamp)).ToList();

        await RefreshStampBinding.RotateAndRebindAsync(
            userRepo, tokenRepo, new Dictionary<Guid, string> { [user.Id] = user.SecurityStamp }, "new-stamp", CancellationToken.None);

        foreach (var before in snapshot)
        {
            var after = tokens.Single(t => t.Id == before.Id);
            Assert.Equal(before.ConsumedAt, after.ConsumedAt);
            Assert.Equal(before.RevokedAt, after.RevokedAt);
            Assert.Equal(before.ReplacedById, after.ReplacedById);
        }

        Assert.Equal("new-stamp", user.SecurityStamp);
        Assert.Equal(["new-stamp"], tokens.Where(t => t.RevokedAt is null).Select(t => t.SecurityStamp).Distinct());
        Assert.True((await Tokens().ValidateAndRotateAsync(next.RawToken)).TryGetSuccess(out _, out _));
    }

    [Fact]
    public async Task Editing_a_role_claim_keeps_every_holders_session_alive_but_not_a_revoked_one()
    {
        var holder = AddUser("holder");
        var role = new Role { Name = "r", NormalizedName = "R", TenantId = Acme };
        role.SetId(Guid.NewGuid());
        roles.Add(role);
        await Roles().AssignRoleAsync(holder.Id, role.Id);
        var live = await Tokens().IssueAsync(holder.Id);
        var revoked = await Tokens().IssueAsync(holder.Id);
        await Tokens().RevokeAsync(revoked.RawToken, allForUser: false);

        await Claims().AddRoleClaimAsync(role.Id, "perm", "x");

        Assert.True((await Tokens().ValidateAndRotateAsync(live.RawToken)).TryGetSuccess(out _, out _));
        Assert.Equal(RefreshOutcome.ReuseDetected, (await Tokens().ValidateAndRotateAsync(revoked.RawToken)).Outcome);
    }
}
