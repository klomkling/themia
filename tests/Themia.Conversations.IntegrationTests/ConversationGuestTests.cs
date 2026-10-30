using Microsoft.Extensions.Logging.Abstractions;
using Themia.Challenges;
using Themia.Conversations.Internal;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>
/// Guest invites, the token exchange, session-proved guest actors and read-time masking on a real PostgreSQL, MySQL and SQL Server. The challenge service is an in-memory
/// double that keeps the documented contract: a token verifies once, for its purpose and tenant, and a newer token for the same scope supersedes the older.
/// </summary>
public abstract class ConversationGuestTests(ConversationEngineFixture fixture)
{
    private static readonly byte[] PepperA = Enumerable.Repeat((byte)7, 32).ToArray();
    private static readonly byte[] PepperB = Enumerable.Repeat((byte)9, 32).ToArray();

    private sealed class AllowAll : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class FakeChallenges : IChallengeService
    {
        private readonly object gate = new();
        private readonly Dictionary<string, ChallengeScope> live = [];
        private readonly Dictionary<ChallengeScope, string> latest = [];

        public bool RateLimit { get; set; }

        public Task<ChallengeIssueResult> IssueAsync(ChallengeScope scope, CancellationToken cancellationToken = default)
        {
            if (RateLimit)
            {
                return Task.FromResult(ChallengeIssueResult.RateLimited());
            }

            var secret = Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
            lock (gate)
            {
                if (latest.TryGetValue(scope, out var old))
                {
                    live.Remove(old); // MaxLiveChallenges = 1: a new link supersedes the earlier one
                }

                live[secret] = scope;
                latest[scope] = secret;
            }

            return Task.FromResult(ChallengeIssueResult.Issued(Guid.NewGuid(), secret, DateTimeOffset.UtcNow.AddHours(72)));
        }

        public Task<ChallengeVerifyResult> VerifyByTokenAsync(string token, string purpose, string? tenantId = null, CancellationToken cancellationToken = default)
        {
            var miss = ChallengeVerifyResult.NotFound(new ChallengeScope(ChallengeScope.UnresolvedKey, purpose, tenantId));
            lock (gate)
            {
                if (!live.TryGetValue(token, out var scope) || scope.Purpose != purpose || scope.TenantId != tenantId)
                {
                    return Task.FromResult(miss);
                }

                live.Remove(token);
                return Task.FromResult(ChallengeVerifyResult.Verified(scope));
            }
        }

        public Task<ChallengeVerifyResult> VerifyAsync(ChallengeScope scope, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ChallengeVerifyResult> PeekAsync(ChallengeScope scope, string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RefundAsync(Guid challengeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingMasker : IConversationMasker
    {
        public ConversationMessage Mask(ConversationActorKind viewer, ConversationMessage message) => throw new TimeoutException("pattern timed out");
    }

    private sealed record Rig(ConversationService Store, ConversationGuests Guests, FakeChallenges Challenges, TimeProvider Time);

    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N");

    private static ConversationActor Member(string userRef) => new(ConversationActorKind.Member, userRef);

    private Rig NewRig(Action<ConversationGuestOptions>? configure = null, IConversationMasker? masker = null, TimeProvider? time = null)
    {
        var guestOptions = new ConversationGuestOptions { CurrentPepperId = "k1" };
        guestOptions.Peppers["k1"] = PepperA;
        configure?.Invoke(guestOptions);
        var options = new ConversationOptions();
        time ??= TimeProvider.System;
        var store = new ConversationService(fixture.Dialect, options, new AllowAll(), new DefaultConversationEventSink(time), masker ?? new RegexConversationMasker(), time, NullLogger<ConversationService>.Instance);
        var challenges = new FakeChallenges();
        return new Rig(store, new ConversationGuests(store, fixture.Dialect, options, guestOptions, challenges, time, NullLogger<ConversationGuests>.Instance), challenges, time);
    }

    private static async Task<(string Tenant, Guid Conversation)> OpenWithMemberAsync(Rig rig)
    {
        var tenant = NewTenant();
        var opened = await rig.Store.OpenAsync(Member("staff"), tenant, new ConversationSubject("booking", "B-1"));
        var joined = await rig.Store.JoinAsync(Member("staff"), tenant, opened.ConversationId!.Value, "Somchai");
        Assert.True(joined.Succeeded);
        return (tenant, opened.ConversationId.Value);
    }

    private static GuestContact Email(string value = "Guest@Example.com") => new(GuestContactKind.Email, value);

    private async Task<(GuestInvite Invite, GuestSessionResult Session)> InviteAndExchangeAsync(Rig rig, string tenant, Guid conversation, GuestContact? contact = null)
    {
        var invite = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, contact ?? Email(), "Guest One");
        Assert.True(invite.Succeeded, invite.Outcome.ToString());
        return (invite, await rig.Guests.ExchangeAsync(tenant, invite.Token!));
    }

    [Fact]
    public async Task Exchange_ShouldOpenASessionTheGuestCanPostWith_AndStoreOnlyItsHash()
    {
        var rig = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(rig);

        var (_, session) = await InviteAndExchangeAsync(rig, tenant, conversation);
        var posted = await rig.Store.PostAsync(ConversationActor.ForGuest(session.SessionKey!), tenant, conversation, new PostMessage("g-1", "hello"));

        Assert.True(session.Succeeded);
        Assert.Equal(conversation, session.ConversationId);
        Assert.Equal(PostOutcome.Posted, posted.Outcome);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_guest_sessions WHERE conversation_id = @Id;", new { Id = conversation }));
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_guest_sessions WHERE conversation_id = @Id AND key_hash = @Plain;", new { Id = conversation, Plain = System.Text.Encoding.ASCII.GetBytes(session.SessionKey!.Value) }));
    }

    [Fact]
    public async Task Exchange_ShouldRefuseAReplayedToken_AWrongTenant_AndGarbage()
    {
        var rig = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(rig);
        var invite = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email(), "Guest One");

        var wrongTenant = await rig.Guests.ExchangeAsync(NewTenant(), invite.Token!);
        var first = await rig.Guests.ExchangeAsync(tenant, invite.Token!);
        var replay = await rig.Guests.ExchangeAsync(tenant, invite.Token!);
        var garbage = await rig.Guests.ExchangeAsync(tenant, "not-a-token");
        var blankTenant = await rig.Guests.ExchangeAsync(" ", invite.Token!);

        Assert.Equal(GuestExchangeOutcome.Refused, wrongTenant.Outcome);
        Assert.True(first.Succeeded);
        Assert.All(new[] { replay, garbage, blankTenant }, r => Assert.Equal(GuestExchangeOutcome.Refused, r.Outcome));
        Assert.All(new[] { replay, garbage, blankTenant, wrongTenant }, r => Assert.Null(r.SessionKey));
    }

    [Fact]
    public async Task Exchange_ShouldRefuse_WhenTheGuestLeftOrTheConversationClosed()
    {
        var rig = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(rig);
        var left = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("a@example.com"), "Guest A");
        var closed = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("b@example.com"), "Guest B");
        await fixture.ExecuteAsync("UPDATE conversation_participants SET left_at = @Now WHERE id = @Id;", new { Id = left.ParticipantId, Now = DateTime.UtcNow });
        await rig.Store.CloseAsync(Member("staff"), tenant, conversation);

        Assert.Equal(GuestExchangeOutcome.Refused, (await rig.Guests.ExchangeAsync(tenant, left.Token!)).Outcome);
        Assert.Equal(GuestExchangeOutcome.Refused, (await rig.Guests.ExchangeAsync(tenant, closed.Token!)).Outcome);
    }

    [Fact]
    public async Task GuestActor_ShouldBeRefused_WhenTheSessionExpiredWasRevokedOrBelongsToAnotherConversation()
    {
        var rig = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(rig);
        var (_, session) = await InviteAndExchangeAsync(rig, tenant, conversation);
        var actor = ConversationActor.ForGuest(session.SessionKey!);
        var other = (await rig.Store.OpenAsync(Member("staff"), tenant, new ConversationSubject("booking", "B-2"))).ConversationId!.Value;
        await rig.Store.JoinAsync(Member("staff"), tenant, other, "Somchai");

        var elsewhere = await rig.Store.PostAsync(actor, tenant, other, new PostMessage("g-1", "hi"));
        var otherTenant = await rig.Store.PostAsync(actor, NewTenant(), conversation, new PostMessage("g-1", "hi"));
        await fixture.ExecuteAsync("UPDATE conversation_guest_sessions SET expires_at = @Past WHERE conversation_id = @Id;", new { Id = conversation, Past = DateTime.UtcNow.AddMinutes(-1) });
        var expired = await rig.Store.PostAsync(actor, tenant, conversation, new PostMessage("g-2", "hi"));
        await fixture.ExecuteAsync("UPDATE conversation_guest_sessions SET expires_at = @Future, revoked_at = @Now WHERE conversation_id = @Id;", new { Id = conversation, Future = DateTime.UtcNow.AddDays(1), Now = DateTime.UtcNow });
        var revoked = await rig.Store.PostAsync(actor, tenant, conversation, new PostMessage("g-3", "hi"));

        Assert.All(new[] { elsewhere, otherTenant, expired, revoked }, r => Assert.Equal(PostOutcome.Refused, r.Outcome));
    }

    [Fact]
    public async Task GuestActor_ShouldBeRefused_ImmediatelyWhenTheParticipantLeaves()
    {
        var rig = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(rig);
        var (invite, session) = await InviteAndExchangeAsync(rig, tenant, conversation);
        var actor = ConversationActor.ForGuest(session.SessionKey!);
        Assert.Equal(PostOutcome.Posted, (await rig.Store.PostAsync(actor, tenant, conversation, new PostMessage("g-1", "hi"))).Outcome);

        await fixture.ExecuteAsync("UPDATE conversation_participants SET left_at = @Now WHERE id = @Id;", new { Id = invite.ParticipantId, Now = DateTime.UtcNow });

        Assert.Equal(PostOutcome.Refused, (await rig.Store.PostAsync(actor, tenant, conversation, new PostMessage("g-2", "hi"))).Outcome);
        Assert.Equal(ListOutcome.Refused, (await rig.Store.ListAsync(actor, tenant, conversation, 0, 10)).Outcome);
    }

    [Fact]
    public async Task Reinvite_ShouldSupersedeTheOldLink_KeepTheOldSessionUntilTheNewLinkIsRedeemed_ThenRevokeIt()
    {
        var rig = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(rig);
        var (first, session) = await InviteAndExchangeAsync(rig, tenant, conversation);
        var oldActor = ConversationActor.ForGuest(session.SessionKey!);

        var again = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("GUEST@example.com"), "Guest One");

        Assert.Equal(GuestInviteOutcome.Reinvited, again.Outcome);
        Assert.Equal(first.ParticipantId, again.ParticipantId);
        Assert.Equal(PostOutcome.Posted, (await rig.Store.PostAsync(oldActor, tenant, conversation, new PostMessage("g-1", "still in"))).Outcome);
        Assert.Equal(GuestExchangeOutcome.Refused, (await rig.Guests.ExchangeAsync(tenant, first.Token!)).Outcome);

        var fresh = await rig.Guests.ExchangeAsync(tenant, again.Token!);

        Assert.True(fresh.Succeeded);
        Assert.Equal(PostOutcome.Refused, (await rig.Store.PostAsync(oldActor, tenant, conversation, new PostMessage("g-2", "revoked"))).Outcome);
        Assert.Equal(PostOutcome.Posted, (await rig.Store.PostAsync(ConversationActor.ForGuest(fresh.SessionKey!), tenant, conversation, new PostMessage("g-3", "new"))).Outcome);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = @Id AND kind = 1;", new { Id = conversation }));
    }

    [Fact]
    public async Task Invite_ShouldFindTheSameGuest_UnderAnOlderPepper_AndMoveItToTheCurrentOne()
    {
        var old = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(old);
        var first = await old.Guests.InviteAsync(Member("staff"), tenant, conversation, Email(), "Guest One");
        var rotated = NewRig(o =>
        {
            o.Peppers["k2"] = PepperB;
            o.CurrentPepperId = "k2";
        });

        var again = await rotated.Guests.InviteAsync(Member("staff"), tenant, conversation, Email(), "Guest One");

        Assert.Equal(GuestInviteOutcome.Reinvited, again.Outcome);
        Assert.Equal(first.ParticipantId, again.ParticipantId);
        Assert.Equal("k2", await fixture.ScalarAsync<string>("SELECT pepper_key_id FROM conversation_participants WHERE id = @Id;", new { Id = first.ParticipantId }));
    }

    [Fact]
    public async Task Invite_ShouldBeRefused_ForAGuestWhoLeft_AndForANonParticipantInviter_AndAGuestInviter()
    {
        var rig = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(rig);
        var first = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email(), "Guest One");
        await fixture.ExecuteAsync("UPDATE conversation_participants SET left_at = @Now WHERE id = @Id;", new { Id = first.ParticipantId, Now = DateTime.UtcNow });
        var session = await rig.Guests.ExchangeAsync(tenant, (await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("other@example.com"), "Guest Two")).Token!);

        var left = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email(), "Guest One");
        var stranger = await rig.Guests.InviteAsync(Member("stranger"), tenant, conversation, Email("x@example.com"), "X");
        var byGuest = await rig.Guests.InviteAsync(ConversationActor.ForGuest(session.SessionKey!), tenant, conversation, Email("y@example.com"), "Y");

        Assert.Equal(GuestInviteOutcome.Refused, left.Outcome);
        Assert.Equal(GuestInviteOutcome.Refused, stranger.Outcome);
        Assert.Equal(GuestInviteOutcome.Refused, byGuest.Outcome);
        Assert.Null(left.Token);
    }

    [Fact]
    public async Task Invite_ShouldCreateOneGuest_WhenTheSameNewContactIsInvitedConcurrently()
    {
        var rig = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(rig);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email(), "Guest One"))));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Outcome.ToString()));
        Assert.Single(results.Select(r => r.ParticipantId).Distinct());
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = @Id AND kind = 1;", new { Id = conversation }));
    }

    [Fact]
    public async Task Invite_ShouldStopAtTheGuestCap_AndReportRateLimitAndInvalidInput()
    {
        var rig = NewRig(o => o.MaxGuestsPerConversation = 2);
        var (tenant, conversation) = await OpenWithMemberAsync(rig);
        await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("a@example.com"), "A");
        await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("b@example.com"), "B");

        var capped = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("c@example.com"), "C");
        var again = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("a@example.com"), "A");
        rig.Challenges.RateLimit = true;
        var limited = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("a@example.com"), "A");
        var invalid = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, new GuestContact(GuestContactKind.Phone, "0812345678"), "P");
        var badName = await rig.Guests.InviteAsync(Member("staff"), tenant, conversation, Email("d@example.com"), "<b>x</b>");

        Assert.Equal(GuestInviteOutcome.LimitReached, capped.Outcome);
        Assert.Equal(GuestInviteOutcome.Reinvited, again.Outcome);
        Assert.Equal(GuestInviteOutcome.RateLimited, limited.Outcome);
        Assert.Equal(GuestInviteOutcome.Invalid, invalid.Outcome);
        Assert.Equal(GuestInviteOutcome.Invalid, badName.Outcome);
    }

    [Fact]
    public async Task List_ShouldMaskContactDetailsAndShowRoleLabelsToAGuest_ButNotToAMember()
    {
        var rig = NewRig();
        var (tenant, conversation) = await OpenWithMemberAsync(rig);
        await fixture.ExecuteAsync("UPDATE conversation_participants SET role_label = 'Front desk' WHERE conversation_id = @Id AND kind = 0;", new { Id = conversation });
        var (_, session) = await InviteAndExchangeAsync(rig, tenant, conversation);
        const string Text = "call 081-234-5678 or mail somchai@example.com, line id: somchai_01, @somchai";
        await rig.Store.PostAsync(Member("staff"), tenant, conversation, new PostMessage("s-1", Text));

        var asGuest = await rig.Store.ListAsync(ConversationActor.ForGuest(session.SessionKey!), tenant, conversation, 0, 10);
        var asMember = await rig.Store.ListAsync(Member("staff"), tenant, conversation, 0, 10);

        var seen = asGuest.Messages.Single();
        Assert.Equal("call [phone hidden] or mail [email hidden] [handle hidden] [handle hidden]", seen.Body);
        Assert.Equal("Front desk", seen.SenderLabel);
        Assert.Equal(Text, asMember.Messages.Single().Body);
        Assert.Equal("Somchai", asMember.Messages.Single().SenderLabel);
        Assert.Equal(Text, await fixture.ScalarAsync<string>("SELECT body FROM conversation_messages WHERE conversation_id = @Id;", new { Id = conversation }));
    }

    [Fact]
    public async Task List_ShouldHideTheBody_WhenTheMaskerFails()
    {
        var rig = NewRig(masker: new ThrowingMasker());
        var (tenant, conversation) = await OpenWithMemberAsync(rig);
        await rig.Store.PostAsync(Member("staff"), tenant, conversation, new PostMessage("s-1", "secret phone 0812345678"));

        var page = await rig.Store.ListAsync(Member("staff"), tenant, conversation, 0, 10);

        Assert.Equal("[message hidden]", page.Messages.Single().Body);
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationGuestTests(PostgresConversationFixture fixture) : ConversationGuestTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationGuestTests(MySqlConversationFixture fixture) : ConversationGuestTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationGuestTests(SqlServerConversationFixture fixture) : ConversationGuestTests(fixture);
