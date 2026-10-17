using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class OtpRecipientResolverTests
{
    private const string Phone = "0812345678";
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Beta = new("beta");

    private readonly List<User> store = [];
    private readonly IdentityModuleOptions options = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-17T10:00:00Z"));

    private OtpRecipientResolver ResolverIn(TenantId? ambient) => new(
        new FakeRepository<User>(store, u => u.Id) { AmbientTenant = ambient }, new TenantContext(ambient), options, clock);

    private User Add(string name, TenantId? tenant, bool active = true, bool confirmed = true, DateTimeOffset? lockoutEnd = null, string phone = Phone)
    {
        var u = new User
        {
            UserName = name, NormalizedUserName = name.ToUpperInvariant(), TenantId = tenant, IsActive = active,
            PhoneNumber = phone, NormalizedPhoneNumber = phone, PhoneNumberConfirmed = confirmed, LockoutEnd = lockoutEnd,
        };
        u.SetId(Guid.NewGuid());
        store.Add(u);
        return u;
    }

    [Fact]
    public async Task Resolve_FindsATenantUserUnderANullScope()
    {
        var user = Add("alice", Acme);

        var recipient = await ResolverIn(null).ResolveAsync(Phone);

        Assert.NotNull(recipient);
        Assert.Equal(user.Id, recipient.UserId);
        Assert.Equal(Acme, recipient.TenantId);
        Assert.Equal(Phone, recipient.PhoneNumber);
    }

    [Fact]
    public async Task Resolve_FindsAPlatformUser_WithNoTenant()
    {
        Add("root", tenant: null);

        var recipient = await ResolverIn(null).ResolveAsync(Phone);

        Assert.NotNull(recipient);
        Assert.Null(recipient.TenantId);
    }

    [Fact]
    public async Task Resolve_IgnoresPlatformUsers_WhenPlatformLoginIsOff()
    {
        options.AllowPlatformLogin = false;
        Add("root", tenant: null);

        Assert.Null(await ResolverIn(null).ResolveAsync(Phone));
    }

    [Fact]
    public async Task Resolve_APlatformHolderDoesNotMakeATenantUserAmbiguous_WhenPlatformLoginIsOff()
    {
        options.AllowPlatformLogin = false;
        var alice = Add("alice", Acme);
        Add("root", tenant: null);

        Assert.Equal(alice.Id, (await ResolverIn(null).ResolveAsync(Phone))?.UserId);
    }

    [Fact]
    public async Task Resolve_ReturnsNullForNoMatch()
    {
        Add("alice", Acme, phone: "0899999999");

        Assert.Null(await ResolverIn(null).ResolveAsync(Phone));
    }

    [Fact]
    public async Task Resolve_SkipsInactiveUnconfirmedAndLockedUsers()
    {
        Add("inactive", Acme, active: false);
        Assert.Null(await ResolverIn(null).ResolveAsync(Phone));
        store.Clear();

        Add("unconfirmed", Acme, confirmed: false);
        Assert.Null(await ResolverIn(null).ResolveAsync(Phone));
        store.Clear();

        Add("locked", Acme, lockoutEnd: clock.GetUtcNow().AddMinutes(5));
        Assert.Null(await ResolverIn(null).ResolveAsync(Phone));
    }

    [Fact]
    public async Task Resolve_ReturnsNullWhenTwoTenantsHoldTheNumber()
    {
        Add("alice", Acme);
        Add("bob", Beta);

        Assert.Null(await ResolverIn(null).ResolveAsync(Phone));
    }

    [Fact]
    public async Task Resolve_AnIneligibleSecondHolderDoesNotMakeItAmbiguous()
    {
        var alice = Add("alice", Acme);
        Add("bob", Beta, active: false);
        Add("carol", Beta, confirmed: false);

        var recipient = await ResolverIn(null).ResolveAsync(Phone);

        Assert.Equal(alice.Id, recipient?.UserId);
    }

    [Fact]
    public async Task Resolve_WithAnAmbientTenant_ConsidersOnlyThatTenantAndThePlatform()
    {
        var alice = Add("alice", Acme);
        Add("bob", Beta);

        var recipient = await ResolverIn(Acme).ResolveAsync(Phone);

        Assert.Equal(alice.Id, recipient?.UserId);
    }

    [Fact]
    public async Task Resolve_WithAnAmbientTenant_IgnoresANumberHeldOnlyElsewhere()
    {
        Add("bob", Beta);

        Assert.Null(await ResolverIn(Acme).ResolveAsync(Phone));
    }

    [Fact]
    public async Task Resolve_WithAnAmbientTenant_ATenantAndAPlatformHolderIsAmbiguous()
    {
        Add("alice", Acme);
        Add("root", tenant: null);

        Assert.Null(await ResolverIn(Acme).ResolveAsync(Phone));
    }

    [Fact]
    public async Task Resolve_RejectsABlankNumber()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ResolverIn(null).ResolveAsync(" "));
    }
}
