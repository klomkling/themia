using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class IdentityAnonymiserTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Beta = new("beta");
    private const string Phone = "+66812345678";

    private readonly List<User> users = [];
    private readonly List<RefreshToken> refreshTokens = [];
    private readonly List<UserToken> userTokens = [];
    private readonly List<UserClaim> claims = [];
    private readonly List<ExternalLoginLink> links = [];
    private readonly List<OtpLoginChallenge> otp = [];
    private readonly FakeUnitOfWork uow = new();
    private readonly RecordingUserLifecycleHooks hooks = new();
    private readonly RecordingIdentityEventObserver observer = new();
    private readonly MemoryCache stampCache = new(new MemoryCacheOptions());

    private IdentityAnonymiser Sut(TenantId? ambient) => new(
        new FakeRepository<User>(users, u => u.Id) { AmbientTenant = ambient },
        new FakeRepository<RefreshToken>(refreshTokens, t => t.Id),
        new FakeRepository<UserToken>(userTokens, t => t.Id),
        new FakeRepository<UserClaim>(claims, c => c.Id),
        new FakeRepository<ExternalLoginLink>(links, l => l.Id) { AmbientTenant = ambient },
        new FakeRepository<OtpLoginChallenge>(otp, c => c.Id) { AmbientTenant = ambient },
        uow, new DataFilterScope(), new TenantContext(ambient), hooks, [observer],
        NullLogger<IdentityAnonymiser>.Instance, stampCache);

    private User AddUser(TenantId? tenant, string name = "alice")
    {
        var u = new User
        {
            TenantId = tenant,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = $"{name}@example.com",
            NormalizedEmail = $"{name}@EXAMPLE.COM",
            EmailConfirmed = true,
            PhoneNumber = Phone,
            NormalizedPhoneNumber = Phone,
            PhoneNumberConfirmed = true,
            PasswordHash = "hash",
            TwoFactorEnabled = true,
        };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u;
    }

    private void AddChildren(User u)
    {
        var parent = new RefreshToken { UserId = u.Id, FamilyId = Guid.NewGuid(), TokenHash = "h1" };
        parent.SetId(Guid.NewGuid());
        var child = new RefreshToken { UserId = u.Id, FamilyId = parent.FamilyId, TokenHash = "h2", ReplacedTokenId = parent.Id };
        child.SetId(Guid.NewGuid());
        var grandChild = new RefreshToken { UserId = u.Id, FamilyId = parent.FamilyId, TokenHash = "h3", ReplacedTokenId = child.Id };
        grandChild.SetId(Guid.NewGuid());
        parent.ReplacedById = child.Id;
        child.ReplacedById = grandChild.Id;
        refreshTokens.AddRange([parent, child, grandChild]);
        var token = new UserToken { UserId = u.Id, Purpose = TokenPurpose.PasswordReset, TokenHash = "t" };
        token.SetId(Guid.NewGuid());
        userTokens.Add(token);
        var claim = new UserClaim { UserId = u.Id, ClaimType = "name", ClaimValue = "Alice Example" };
        claim.SetId(Guid.NewGuid());
        claims.Add(claim);
        var link = new ExternalLoginLink { TenantId = u.TenantId, UserId = u.Id, Provider = "line", ExternalId = "U123" };
        link.SetId(Guid.NewGuid());
        links.Add(link);
        otp.Add(new OtpLoginChallenge { Id = Guid.NewGuid(), TenantId = u.TenantId, UserId = u.Id, NormalizedPhoneNumber = Phone });
        // Issued before the user was resolved: keyed only by the phone the user held.
        otp.Add(new OtpLoginChallenge { Id = Guid.NewGuid(), TenantId = u.TenantId, UserId = null, NormalizedPhoneNumber = Phone });
    }

    [Fact]
    public async Task Anonymise_scrubs_the_row_and_keeps_the_id()
    {
        var u = AddUser(Acme);
        var oldStamp = u.SecurityStamp;

        var result = await Sut(Acme).AnonymiseAsync(u.Id);

        Assert.Equal(AnonymiseOutcome.Anonymised, result.Outcome);
        var row = Assert.Single(users);
        Assert.Equal(u.Id, row.Id);
        Assert.Equal($"anonymised-{u.Id:N}", row.UserName);
        Assert.Equal($"anonymised-{u.Id:N}".ToUpperInvariant(), row.NormalizedUserName);
        Assert.Null(row.Email);
        Assert.Null(row.NormalizedEmail);
        Assert.Null(row.PhoneNumber);
        Assert.Null(row.NormalizedPhoneNumber);
        Assert.Null(row.PasswordHash);
        Assert.False(row.EmailConfirmed);
        Assert.False(row.PhoneNumberConfirmed);
        Assert.False(row.TwoFactorEnabled);
        Assert.False(row.IsActive);
        Assert.False(row.IsDeleted);
        Assert.NotEqual(oldStamp, row.SecurityStamp);
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task Anonymise_deletes_every_child_row_including_otp_keyed_only_by_the_old_phone()
    {
        var u = AddUser(Acme);
        var bystander = AddUser(Acme, "bob");
        AddChildren(u);
        var bystanderClaim = new UserClaim { UserId = bystander.Id, ClaimType = "a", ClaimValue = "b" };
        bystanderClaim.SetId(Guid.NewGuid());
        claims.Add(bystanderClaim);

        await Sut(Acme).AnonymiseAsync(u.Id);

        Assert.Empty(refreshTokens);
        Assert.Empty(userTokens);
        Assert.Empty(links);
        Assert.Empty(otp);
        Assert.Equal(bystanderClaim, Assert.Single(claims));
        Assert.Equal("bob@example.com", bystander.Email);
    }

    [Fact]
    public async Task Anonymise_keeps_role_memberships_and_does_not_collide_for_two_users()
    {
        var a = AddUser(Acme, "a");
        var b = AddUser(Acme, "b");

        await Sut(Acme).AnonymiseAsync(a.Id);
        await Sut(Acme).AnonymiseAsync(b.Id);

        Assert.NotEqual(a.UserName, b.UserName);
    }

    [Fact]
    public async Task Second_call_is_AlreadyAnonymised_writes_no_user_change_and_raises_no_event()
    {
        var u = AddUser(Acme);
        await Sut(Acme).AnonymiseAsync(u.Id);
        observer.Mutations.Clear();
        var stamp = u.SecurityStamp;

        var again = await Sut(Acme).AnonymiseAsync(u.Id);

        Assert.Equal(AnonymiseOutcome.AlreadyAnonymised, again.Outcome);
        Assert.True(again.Succeeded);
        Assert.Equal(stamp, u.SecurityStamp);
        Assert.Empty(observer.Mutations);
    }

    [Fact]
    public async Task Second_call_still_removes_children_that_reappeared()
    {
        var u = AddUser(Acme);
        await Sut(Acme).AnonymiseAsync(u.Id);
        AddChildren(u);

        var again = await Sut(Acme).AnonymiseAsync(u.Id);

        Assert.Equal(AnonymiseOutcome.AlreadyAnonymised, again.Outcome);
        Assert.Empty(refreshTokens);
        Assert.Empty(links);
        Assert.Empty(userTokens);
    }

    [Fact]
    public async Task A_user_who_merely_looks_like_a_tombstone_is_not_one()
    {
        var u = AddUser(Acme, "anonymised-someone");

        var result = await Sut(Acme).AnonymiseAsync(u.Id);

        Assert.Equal(AnonymiseOutcome.Anonymised, result.Outcome);
    }

    [Fact]
    public async Task Unknown_id_is_NotFound()
    {
        var result = await Sut(Acme).AnonymiseAsync(Guid.NewGuid());

        Assert.Equal(AnonymiseOutcome.NotFound, result.Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Other_tenants_user_is_NotFound_and_untouched()
    {
        var other = AddUser(Beta);
        AddChildren(other);

        var result = await Sut(Acme).AnonymiseAsync(other.Id);

        Assert.Equal(AnonymiseOutcome.NotFound, result.Outcome);
        Assert.Equal("alice@example.com", other.Email);
        Assert.Equal(3, refreshTokens.Count);
        Assert.Single(links);
    }

    [Fact]
    public async Task Platform_user_is_NotFound_from_a_tenant_scope()
    {
        var platform = AddUser(null, "root");
        AddChildren(platform);

        var result = await Sut(Acme).AnonymiseAsync(platform.Id);

        Assert.Equal(AnonymiseOutcome.NotFound, result.Outcome);
        Assert.Equal("root@example.com", platform.Email);
        Assert.Equal(3, refreshTokens.Count);
    }

    [Fact]
    public async Task Platform_user_is_anonymised_from_the_platform_scope_and_every_child_goes()
    {
        var platform = AddUser(null, "root");
        AddChildren(platform);

        var result = await Sut(null).AnonymiseAsync(platform.Id);

        Assert.Equal(AnonymiseOutcome.Anonymised, result.Outcome);
        Assert.Empty(refreshTokens);
        Assert.Empty(links);
        Assert.Empty(otp);
    }

    [Fact]
    public async Task Veto_hook_refuses_and_writes_nothing()
    {
        var u = AddUser(Acme);
        AddChildren(u);
        hooks.RefuseAnonymise = "legal hold";

        var result = await Sut(Acme).AnonymiseAsync(u.Id);

        Assert.Equal(AnonymiseOutcome.Refused, result.Outcome);
        Assert.Equal("legal hold", result.Reason);
        Assert.Equal("alice@example.com", u.Email);
        Assert.Equal(3, refreshTokens.Count);
        Assert.Equal(0, uow.SaveCount);
        Assert.Equal((u.Id, UserMutation.Anonymised, "legal hold"), Assert.Single(observer.Refusals));
    }

    [Fact]
    public async Task Anonymise_announces_the_mutation_with_the_id_only()
    {
        var u = AddUser(Acme);

        await Sut(Acme).AnonymiseAsync(u.Id);

        Assert.Equal(UserMutation.Anonymised, Assert.Single(hooks.Observed));
        Assert.Equal((u.Id, UserMutation.Anonymised), Assert.Single(observer.Mutations));
    }

    [Fact]
    public async Task Anonymise_evicts_the_cached_security_stamp()
    {
        var u = AddUser(Acme);
        stampCache.Set(SecurityStampCache.Key(u.Id), (string?)u.SecurityStamp);

        await Sut(Acme).AnonymiseAsync(u.Id);

        Assert.False(stampCache.TryGetValue(SecurityStampCache.Key(u.Id), out _));
    }
}
