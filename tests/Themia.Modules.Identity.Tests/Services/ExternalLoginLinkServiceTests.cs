using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

/// <summary>
/// Attaching, detaching and listing external identities on an existing user (coord #0135). The rules that
/// matter are the refusals: an identity is never moved between users, never bound to an inactive account,
/// and a consumer's hook can veto either direction.
/// </summary>
public sealed class ExternalLoginLinkServiceTests
{
    private static readonly TenantId Acme = new("acme");

    private readonly List<User> userStore = [];
    private readonly List<ExternalLoginLink> linkStore = [];
    private readonly FakeRepository<User> users;
    private readonly FakeRepository<ExternalLoginLink> links;
    private readonly FakeUnitOfWork uow = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-09-19T00:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly ScriptedHooks hooks = new();
    private readonly RecordingObserver observer = new();

    public ExternalLoginLinkServiceTests()
    {
        users = new FakeRepository<User>(userStore, u => u.Id) { AmbientTenant = Acme };
        links = new FakeRepository<ExternalLoginLink>(linkStore, l => l.Id) { AmbientTenant = Acme };
    }

    private ExternalLoginLinkService Build(IRepository<ExternalLoginLink, Guid>? linkRepo = null) =>
        new(users, linkRepo ?? links, uow, clock, new DataFilterScope(), options, hooks, [observer]);

    private ExternalLoginLinkService BuildWith(IUserLifecycleHooks rules) =>
        new(users, links, uow, clock, new DataFilterScope(), options, rules, [observer]);

    private User Seed(string name = "alice", bool active = true)
    {
        var user = new User { UserName = name, NormalizedUserName = name.ToUpperInvariant(), TenantId = Acme, IsActive = active };
        user.SetId(Guid.CreateVersion7());
        userStore.Add(user);
        return user;
    }

    private static ExternalIdentity Telegram(string subject) =>
        new("Telegram", subject, Email: null, EmailVerified: false, DisplayName: "tg");

    // ---- link ----------------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_attaches_the_identity_lower_cased_and_announces_it()
    {
        var alice = Seed();

        var result = await Build().LinkAsync(alice.Id, Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.Linked, result.Outcome);
        var link = Assert.Single(linkStore);
        Assert.Equal(("telegram", "tg-1", alice.Id), (link.Provider, link.ExternalId, link.UserId));
        Assert.Equal(clock.GetUtcNow(), link.CreatedAt);
        Assert.Equal([UserMutation.ExternalLogin], hooks.Mutated);
        Assert.Equal([UserMutation.ExternalLogin], observer.Mutated);
    }

    [Fact]
    public async Task LinkAsync_is_idempotent_for_the_same_user_and_writes_nothing()
    {
        var alice = Seed();
        var sut = Build();
        await sut.LinkAsync(alice.Id, Telegram("tg-1"));
        hooks.Mutated.Clear();

        var again = await sut.LinkAsync(alice.Id, Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.AlreadyLinkedToUser, again.Outcome);
        Assert.True(again.IsLinked);
        Assert.Single(linkStore);
        Assert.Empty(hooks.Mutated);
    }

    [Fact]
    public async Task LinkAsync_never_moves_an_identity_from_another_user()
    {
        var alice = Seed("alice");
        var bob = Seed("bob");
        var sut = Build();
        await sut.LinkAsync(alice.Id, Telegram("tg-1"));

        var stolen = await sut.LinkAsync(bob.Id, Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.LinkedToAnotherUser, stolen.Outcome);
        Assert.False(stolen.IsLinked);
        Assert.Equal(alice.Id, Assert.Single(linkStore).UserId);
    }

    [Fact]
    public async Task LinkAsync_refuses_a_deactivated_user()
    {
        // Otherwise a later reactivation inherits a sign-in method nobody approved — the same rule
        // ResolveOrProvisionAsync applies to its auto-link.
        var inactive = Seed(active: false);

        var result = await Build().LinkAsync(inactive.Id, Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.UserInactive, result.Outcome);
        Assert.Empty(linkStore);
    }

    [Fact]
    public async Task LinkAsync_refuses_a_locked_out_user()
    {
        var locked = Seed();
        locked.LockoutEnd = clock.GetUtcNow().AddMinutes(5);

        var result = await Build().LinkAsync(locked.Id, Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.UserInactive, result.Outcome);
        Assert.Empty(linkStore);
    }

    [Fact]
    public async Task LinkAsync_reports_an_unknown_user()
    {
        var result = await Build().LinkAsync(Guid.CreateVersion7(), Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.UserNotFound, result.Outcome);
    }

    [Fact]
    public async Task LinkAsync_refused_by_a_hook_writes_nothing_and_carries_the_reason()
    {
        var alice = Seed();
        hooks.RefuseLink = "one Telegram account per user";

        var result = await Build().LinkAsync(alice.Id, Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.Refused, result.Outcome);
        Assert.Equal("one Telegram account per user", result.Reason);
        Assert.Empty(linkStore);
        Assert.Equal([(UserMutation.ExternalLogin, "one Telegram account per user")], observer.Refused);
        Assert.Empty(hooks.Mutated);
    }

    [Fact]
    public async Task LinkAsync_hands_the_hook_the_normalized_provider_and_subject()
    {
        var alice = Seed();

        await Build().LinkAsync(alice.Id, Telegram("tg-1"));

        Assert.Equal((alice.Id, "telegram", "tg-1"), hooks.LastLink);
    }

    [Fact]
    public async Task LinkAsync_does_not_ask_the_hook_about_an_idempotent_relink()
    {
        // A consumer's "one identity per provider" hook would otherwise refuse the retry of a link it
        // already allowed — the user now holds a Telegram identity, the very one being re-linked.
        var alice = Seed();
        var sut = Build();
        await sut.LinkAsync(alice.Id, Telegram("tg-1"));
        hooks.RefuseLink = "already has a Telegram identity";

        var again = await sut.LinkAsync(alice.Id, Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.AlreadyLinkedToUser, again.Outcome);
    }

    [Fact]
    public async Task LinkAsync_never_touches_the_users_email()
    {
        var alice = Seed();
        alice.Email = "alice@example.com";
        alice.EmailConfirmed = true;

        await Build().LinkAsync(
            alice.Id, new ExternalIdentity("telegram", "tg-1", "someone-else@example.com", EmailVerified: true, "tg"));

        Assert.Equal("alice@example.com", alice.Email);
        Assert.True(alice.EmailConfirmed);
    }

    [Fact]
    public async Task LinkAsync_that_loses_the_insert_race_answers_with_the_winner()
    {
        // Two concurrent links of one identity to different users: exactly one wins. The loser's insert
        // violates the unique index, and the answer must be LinkedToAnotherUser, not an exception.
        var alice = Seed("alice");
        var bob = Seed("bob");
        var racing = new RacingLinkRepository(links, linkStore, winnerUserId: alice.Id);

        var result = await Build(racing).LinkAsync(bob.Id, Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.LinkedToAnotherUser, result.Outcome);
        Assert.Equal(alice.Id, Assert.Single(linkStore).UserId);
        Assert.Empty(hooks.Mutated);
    }

    [Fact]
    public async Task LinkAsync_that_loses_the_race_to_itself_answers_already_linked()
    {
        var alice = Seed("alice");
        var racing = new RacingLinkRepository(links, linkStore, winnerUserId: alice.Id);

        var result = await Build(racing).LinkAsync(alice.Id, Telegram("tg-1"));

        Assert.Equal(ExternalLoginLinkOutcome.AlreadyLinkedToUser, result.Outcome);
    }

    // ---- unlink --------------------------------------------------------------------------------

    [Fact]
    public async Task UnlinkAsync_removes_every_link_for_the_provider_and_only_that_provider()
    {
        var alice = Seed();
        var sut = Build();
        await sut.LinkAsync(alice.Id, Telegram("tg-1"));
        await sut.LinkAsync(alice.Id, Telegram("tg-2"));
        await sut.LinkAsync(alice.Id, new ExternalIdentity("line", "ln-1", null, false, "ln"));
        hooks.Mutated.Clear();

        var result = await sut.UnlinkAsync(alice.Id, "TELEGRAM");

        Assert.Equal(ExternalLoginUnlinkOutcome.Unlinked, result.Outcome);
        Assert.Equal("line", Assert.Single(linkStore).Provider);
        Assert.Equal([UserMutation.ExternalLogin], hooks.Mutated);
    }

    [Fact]
    public async Task UnlinkAsync_with_nothing_to_remove_is_not_linked_and_asks_no_hook()
    {
        var alice = Seed();
        hooks.RefuseUnlink = "would never be consulted";

        var result = await Build().UnlinkAsync(alice.Id, "telegram");

        Assert.Equal(ExternalLoginUnlinkOutcome.NotLinked, result.Outcome);
        Assert.Null(hooks.LastUnlink);
    }

    [Fact]
    public async Task UnlinkAsync_refused_by_a_hook_keeps_the_link()
    {
        // Where "never leave a user with no way to sign in" lives — Themia cannot compute "last".
        var alice = Seed();
        var sut = Build();
        await sut.LinkAsync(alice.Id, Telegram("tg-1"));
        hooks.RefuseUnlink = "this is the only way you can sign in";

        var result = await sut.UnlinkAsync(alice.Id, "telegram");

        Assert.Equal(ExternalLoginUnlinkOutcome.Refused, result.Outcome);
        Assert.Equal("this is the only way you can sign in", result.Reason);
        Assert.Single(linkStore);
        Assert.Equal((alice.Id, "telegram"), hooks.LastUnlink);
    }

    [Fact]
    public async Task UnlinkAsync_reports_an_unknown_user()
    {
        var result = await Build().UnlinkAsync(Guid.CreateVersion7(), "telegram");

        Assert.Equal(ExternalLoginUnlinkOutcome.UserNotFound, result.Outcome);
    }

    // ---- current links and refusal codes (coord #0139) ------------------------------------------

    [Fact]
    public async Task LinkAsync_hands_the_hook_every_link_the_user_holds_before_the_new_one()
    {
        // "One identity per provider" needs the user's links, and the hook cannot read them itself: the
        // only supported read is this service, which is the one calling the hook.
        var alice = Seed();
        var sut = Build();
        await sut.LinkAsync(alice.Id, Telegram("tg-1"));
        await sut.LinkAsync(alice.Id, new ExternalIdentity("line", "ln-1", null, false, "ln"));
        var rules = new SnapshotHooks();

        await BuildWith(rules).LinkAsync(alice.Id, Telegram("tg-2"));

        Assert.Equal(["telegram:tg-1", "line:ln-1"], rules.SeenOnLink!.Select(l => $"{l.Provider}:{l.Subject}"));
    }

    [Fact]
    public async Task UnlinkAsync_hands_the_hook_every_link_the_user_holds_not_only_the_providers()
    {
        // "Never unlink the last channel" is a question about the OTHER providers.
        var alice = Seed();
        var sut = Build();
        await sut.LinkAsync(alice.Id, Telegram("tg-1"));
        await sut.LinkAsync(alice.Id, new ExternalIdentity("line", "ln-1", null, false, "ln"));
        var rules = new SnapshotHooks();

        await BuildWith(rules).UnlinkAsync(alice.Id, "telegram");

        Assert.Equal(["telegram", "line"], rules.SeenOnUnlink!.Select(l => l.Provider));
    }

    [Fact]
    public async Task A_rule_over_the_current_links_refuses_the_last_channel_with_its_code()
    {
        var alice = Seed();
        await Build().LinkAsync(alice.Id, Telegram("tg-1"));
        var sut = BuildWith(new SnapshotHooks());

        var result = await sut.UnlinkAsync(alice.Id, "telegram");

        Assert.Equal(ExternalLoginUnlinkOutcome.Refused, result.Outcome);
        Assert.Equal("last_channel", result.RefusalCode);
        Assert.Equal("this is your only channel", result.Reason);
        Assert.Single(linkStore);
    }

    [Fact]
    public async Task A_rule_over_the_current_links_refuses_a_second_identity_with_its_code()
    {
        var alice = Seed();
        await Build().LinkAsync(alice.Id, Telegram("tg-1"));
        var sut = BuildWith(new SnapshotHooks());

        var result = await sut.LinkAsync(alice.Id, Telegram("tg-2"));

        Assert.Equal(ExternalLoginLinkOutcome.Refused, result.Outcome);
        Assert.Equal("channel_already_linked", result.RefusalCode);
        Assert.Single(linkStore);
    }

    [Fact]
    public async Task A_refusal_without_a_code_has_no_code()
    {
        var alice = Seed();
        hooks.RefuseLink = "no";

        var result = await Build().LinkAsync(alice.Id, Telegram("tg-1"));

        Assert.Null(result.RefusalCode);
    }

    // ---- list and look up ----------------------------------------------------------------------

    [Fact]
    public async Task GetLoginsAsync_lists_the_users_links_with_when_each_was_made()
    {
        var alice = Seed();
        var sut = Build();
        await sut.LinkAsync(alice.Id, new ExternalIdentity("line", "ln-1", null, false, "ln"));
        clock.Advance(TimeSpan.FromDays(1));
        await sut.LinkAsync(alice.Id, Telegram("tg-1"));

        var logins = await sut.GetLoginsAsync(alice.Id);

        Assert.Equal(
            [
                new ExternalLoginInfo("line", "ln-1", DateTimeOffset.Parse("2026-09-19T00:00:00Z")),
                new ExternalLoginInfo("telegram", "tg-1", DateTimeOffset.Parse("2026-09-20T00:00:00Z")),
            ],
            logins);
    }

    [Fact]
    public async Task GetLoginsAsync_for_an_unknown_user_is_empty()
    {
        Assert.Empty(await Build().GetLoginsAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task GetLoginsForUsersAsync_returns_entries_only_for_users_with_links()
    {
        var alice = Seed("alice");
        var bob = Seed("bob");
        var carol = Seed("carol");
        var sut = Build();
        await sut.LinkAsync(alice.Id, Telegram("tg-a"));
        await sut.LinkAsync(bob.Id, Telegram("tg-b"));

        var map = await sut.GetLoginsForUsersAsync([alice.Id, bob.Id, carol.Id, alice.Id]);

        Assert.Equal(2, map.Count);
        Assert.Equal("tg-a", Assert.Single(map[alice.Id]).Subject);
        Assert.Equal("tg-b", Assert.Single(map[bob.Id]).Subject);
        Assert.False(map.ContainsKey(carol.Id));
    }

    [Fact]
    public async Task GetLoginsForUsersAsync_of_nothing_is_empty()
    {
        Assert.Empty(await Build().GetLoginsForUsersAsync([]));
    }

    [Fact]
    public async Task GetLoginsForUsersAsync_refuses_more_than_the_batch_cap()
    {
        var tooMany = Enumerable.Range(0, IExternalLoginLinkService.MaxBatchSize + 1)
            .Select(_ => Guid.CreateVersion7()).ToArray();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Build().GetLoginsForUsersAsync(tooMany));
    }

    [Fact]
    public async Task GetLoginsForUsersAsync_counts_distinct_ids_against_the_cap()
    {
        // A caller passing each recipient of a batch, duplicates included, must not be refused for them.
        var same = Guid.CreateVersion7();
        var repeated = Enumerable.Repeat(same, IExternalLoginLinkService.MaxBatchSize + 50).ToArray();

        Assert.Empty(await Build().GetLoginsForUsersAsync(repeated));
    }

    [Fact]
    public async Task FindUserByLoginAsync_finds_the_owner_without_provisioning()
    {
        var alice = Seed();
        var sut = Build();
        await sut.LinkAsync(alice.Id, Telegram("tg-1"));
        var before = userStore.Count;

        var owner = await sut.FindUserByLoginAsync("TELEGRAM", "tg-1");
        var unknown = await sut.FindUserByLoginAsync("telegram", "tg-unknown");

        Assert.Equal(alice.Id, owner?.Id);
        Assert.Null(unknown);
        Assert.Equal(before, userStore.Count);
        Assert.Single(linkStore);
    }

    [Fact]
    public async Task Another_tenants_identity_is_invisible()
    {
        var alice = Seed();
        await Build().LinkAsync(alice.Id, Telegram("tg-1"));
        users.AmbientTenant = new TenantId("globex");
        links.AmbientTenant = new TenantId("globex");

        var sut = Build();

        Assert.Null(await sut.FindUserByLoginAsync("telegram", "tg-1"));
        Assert.Empty(await sut.GetLoginsAsync(alice.Id));
        Assert.Equal(ExternalLoginUnlinkOutcome.UserNotFound, (await sut.UnlinkAsync(alice.Id, "telegram")).Outcome);
    }

    // ---- platform users reached from a tenant scope ---------------------------------------------
    //
    // A link made from a tenant scope lands in that tenant's partition whoever the user is: the data layer
    // stamps the ambient tenant on insert (FakeRepository mirrors it). Review once proposed checking the
    // platform partition for a platform user instead; on the real engines that checks a partition the insert
    // never reaches, and the relink violates the tenant index. These pin the ambient-partition model; the
    // conformance suite pins the same thing on all four engines.

    private User SeedPlatform(string name)
    {
        var user = new User { UserName = name, NormalizedUserName = name.ToUpperInvariant(), TenantId = null, IsActive = true };
        user.SetId(Guid.CreateVersion7());
        userStore.Add(user);
        return user;
    }

    private void SeedPlatformLink(Guid userId, string provider, string subject)
    {
        var link = new ExternalLoginLink
        {
            UserId = userId, Provider = provider, ExternalId = subject, TenantId = null, CreatedAt = clock.GetUtcNow(),
        };
        link.SetId(Guid.CreateVersion7());
        linkStore.Add(link);
    }

    [Fact]
    public async Task A_platform_user_linked_from_a_tenant_scope_gets_that_tenants_link_and_relinks_idempotently()
    {
        options.AllowPlatformLogin = false;
        var admin = SeedPlatform("admin");
        var sut = Build();

        var first = await sut.LinkAsync(admin.Id, Telegram("tg-admin"));
        var again = await sut.LinkAsync(admin.Id, Telegram("tg-admin"));

        Assert.Equal(ExternalLoginLinkOutcome.Linked, first.Outcome);
        Assert.Equal(ExternalLoginLinkOutcome.AlreadyLinkedToUser, again.Outcome);
        Assert.Equal(Acme, Assert.Single(linkStore).TenantId);
    }

    [Fact]
    public async Task A_platform_owned_identity_is_refused_when_this_scopes_sign_in_would_resolve_it()
    {
        // With platform sign-in on, signing in here with this identity reaches the platform owner — so it
        // already belongs to somebody, from this scope's point of view.
        options.AllowPlatformLogin = true;
        var owner = SeedPlatform("owner");
        var other = SeedPlatform("other");
        SeedPlatformLink(owner.Id, "telegram", "tg-owned");

        var result = await Build().LinkAsync(other.Id, Telegram("tg-owned"));

        Assert.Equal(ExternalLoginLinkOutcome.LinkedToAnotherUser, result.Outcome);
        Assert.Single(linkStore);
    }

    [Fact]
    public async Task Listing_and_unlinking_from_a_tenant_scope_never_reach_the_platforms_own_links()
    {
        // Batch and single agree — review found them disagreeing about exactly this user — and neither
        // exposes, nor unlinks, a link the tenant does not own.
        var admin = SeedPlatform("admin");
        SeedPlatformLink(admin.Id, "line", "ln-global");
        var sut = Build();
        await sut.LinkAsync(admin.Id, Telegram("tg-acme"));

        var single = await sut.GetLoginsAsync(admin.Id);
        var batch = await sut.GetLoginsForUsersAsync([admin.Id]);
        var unlinkGlobal = await sut.UnlinkAsync(admin.Id, "line");

        Assert.Equal("tg-acme", Assert.Single(single).Subject);
        Assert.Equal(single, batch[admin.Id]);
        Assert.Equal(ExternalLoginUnlinkOutcome.NotLinked, unlinkGlobal.Outcome);
        Assert.Contains(linkStore, l => l.ExternalId == "ln-global");
    }

    // ---- doubles -------------------------------------------------------------------------------

    /// <summary>Opsezy's two channel rules (coord #0139), written against the overloads that carry the
    /// user's current links.</summary>
    private sealed class SnapshotHooks : IUserLifecycleHooks
    {
        public IReadOnlyList<ExternalLoginInfo>? SeenOnLink { get; private set; }
        public IReadOnlyList<ExternalLoginInfo>? SeenOnUnlink { get; private set; }

        public ValueTask<UserMutationDecision> OnBeforeLinkExternalLoginAsync(
            Guid userId, string provider, string subject, IReadOnlyList<ExternalLoginInfo> currentLogins,
            CancellationToken cancellationToken)
        {
            SeenOnLink = currentLogins;
            return ValueTask.FromResult(currentLogins.Any(l => l.Provider == provider)
                ? UserMutationDecision.Refuse("you already linked this channel", "channel_already_linked")
                : UserMutationDecision.Allow());
        }

        public ValueTask<UserMutationDecision> OnBeforeUnlinkExternalLoginAsync(
            Guid userId, string provider, IReadOnlyList<ExternalLoginInfo> currentLogins,
            CancellationToken cancellationToken)
        {
            SeenOnUnlink = currentLogins;
            return ValueTask.FromResult(currentLogins.All(l => l.Provider == provider)
                ? UserMutationDecision.Refuse("this is your only channel", "last_channel")
                : UserMutationDecision.Allow());
        }
    }

    /// <summary>Implements the original overloads only — every pre-#0139 implementation looks like this, and
    /// the tests using it prove the service still reaches them through the new overloads' defaults.</summary>
    private sealed class ScriptedHooks : IUserLifecycleHooks
    {
        public string? RefuseLink { get; set; }
        public string? RefuseUnlink { get; set; }
        public (Guid, string, string)? LastLink { get; private set; }
        public (Guid, string)? LastUnlink { get; private set; }
        public List<UserMutation> Mutated { get; } = [];

        public ValueTask<UserMutationDecision> OnBeforeLinkExternalLoginAsync(
            Guid userId, string provider, string subject, CancellationToken cancellationToken = default)
        {
            LastLink = (userId, provider, subject);
            return ValueTask.FromResult(RefuseLink is null ? UserMutationDecision.Allow() : UserMutationDecision.Refuse(RefuseLink));
        }

        public ValueTask<UserMutationDecision> OnBeforeUnlinkExternalLoginAsync(
            Guid userId, string provider, CancellationToken cancellationToken = default)
        {
            LastUnlink = (userId, provider);
            return ValueTask.FromResult(RefuseUnlink is null ? UserMutationDecision.Allow() : UserMutationDecision.Refuse(RefuseUnlink));
        }

        public ValueTask OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default)
        {
            Mutated.Add(mutation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingObserver : IIdentityEventObserver
    {
        public List<UserMutation> Mutated { get; } = [];
        public List<(UserMutation, string)> Refused { get; } = [];

        public Task OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default)
        {
            Mutated.Add(mutation);
            return Task.CompletedTask;
        }

        public Task OnUserMutationRefusedAsync(
            Guid userId, UserMutation mutation, string reason, CancellationToken cancellationToken = default)
        {
            Refused.Add((mutation, reason));
            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for a concurrent request that commits a link for the same identity between this
    /// request's ownership check and its insert — the insert then violates the unique index, as it would on
    /// a real database. Every other operation reads the real in-memory store.</summary>
    private sealed class RacingLinkRepository(
        IRepository<ExternalLoginLink, Guid> inner, List<ExternalLoginLink> store, Guid winnerUserId)
        : IRepository<ExternalLoginLink, Guid>
    {
        public Task AddAsync(ExternalLoginLink entity, CancellationToken cancellationToken = default)
        {
            var winner = new ExternalLoginLink
            {
                UserId = winnerUserId,
                Provider = entity.Provider,
                ExternalId = entity.ExternalId,
                TenantId = entity.TenantId,
                CreatedAt = entity.CreatedAt,
            };
            winner.SetId(Guid.CreateVersion7());
            store.Add(winner);
            throw new UniqueConstraintException("ux_external_logins_tenant_provider_external_id");
        }

        public Task<ExternalLoginLink?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);
        public Task<IReadOnlyList<ExternalLoginLink>> ListAsync(ISpecification<ExternalLoginLink> specification, CancellationToken cancellationToken = default) => inner.ListAsync(specification, cancellationToken);
        public Task<ExternalLoginLink?> FirstOrDefaultAsync(ISpecification<ExternalLoginLink> specification, CancellationToken cancellationToken = default) => inner.FirstOrDefaultAsync(specification, cancellationToken);
        public Task<long> CountAsync(ISpecification<ExternalLoginLink> specification, CancellationToken cancellationToken = default) => inner.CountAsync(specification, cancellationToken);
        public Task<bool> AnyAsync(ISpecification<ExternalLoginLink> specification, CancellationToken cancellationToken = default) => inner.AnyAsync(specification, cancellationToken);
        public Task<PagedResult<ExternalLoginLink>> PageAsync(ISpecification<ExternalLoginLink> specification, CancellationToken cancellationToken = default) => inner.PageAsync(specification, cancellationToken);
        public void Update(ExternalLoginLink entity) => inner.Update(entity);
        public void Remove(ExternalLoginLink entity) => inner.Remove(entity);
        public Task<int> UpdateWhereAsync(ISpecification<ExternalLoginLink> specification, Action<IBulkUpdateSetters<ExternalLoginLink>> set, CancellationToken cancellationToken = default) => inner.UpdateWhereAsync(specification, set, cancellationToken);
    }
}
