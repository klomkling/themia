using System.Data.Common;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Conversations.Internal;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>Staff oversight read and its audit on a real PostgreSQL, MySQL and SQL Server.</summary>
public abstract class ConversationOversightTests(ConversationEngineFixture fixture)
{
    private sealed class AllowAll : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class DenyOversight : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(action != ConversationAction.OversightRead);
    }

    /// <summary>Records every entry; can write a row through the passed transaction first, and can fail.</summary>
    private sealed class SpyAudit(string? table = null) : IConversationReadAudit
    {
        public List<OversightRead> Entries { get; } = [];

        public Exception? Fail { get; set; }

        /// <summary>Runs a failing statement on the transaction and swallows the error, like a port that dedupes on a unique violation.</summary>
        public bool SwallowSqlError { get; set; }

        public async Task RecordAsync(DbConnection connection, DbTransaction transaction, OversightRead entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            if (table is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition($"INSERT INTO {table} (id) VALUES (@Id);", new { Id = entry.ConversationId.ToString("N") }, transaction, cancellationToken: cancellationToken));
            }

            if (SwallowSqlError)
            {
                try
                {
                    await connection.ExecuteAsync(new CommandDefinition("SELECT * FROM table_that_does_not_exist_oversight;", transaction: transaction, cancellationToken: cancellationToken));
                }
                catch (DbException)
                {
                }
            }

            if (Fail is not null)
            {
                throw Fail;
            }
        }
    }

    /// <summary>Tags every body with the viewer kind, so a test can see that the masker ran and for whom; throws on a body containing "boom".</summary>
    private sealed class TaggingMasker : IConversationMasker
    {
        public ConversationMessage Mask(ConversationActorKind viewer, ConversationMessage message) =>
            message.Body is { } body && body.Contains("boom", StringComparison.Ordinal) ? throw new InvalidOperationException("masker failed") : message with { Body = message.Body is null ? null : $"[{viewer}]{message.Body}" };
    }

    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N");

    private static ConversationActor Member(string userRef) => new(ConversationActorKind.Member, userRef);

    private ConversationService Store(IConversationAccessPolicy? policy = null, IConversationMasker? masker = null) =>
        new(fixture.Dialect, new ConversationOptions(), policy ?? new AllowAll(), new DefaultConversationEventSink(TimeProvider.System), masker ?? new RegexConversationMasker(), TimeProvider.System, NullLogger<ConversationService>.Instance);

    private ConversationOversightService Oversight(ConversationService store, IConversationReadAudit? audit, TimeProvider? time = null) =>
        new(store, audit, time ?? TimeProvider.System, NullLogger<ConversationOversightService>.Instance);

    private async Task<Guid> RoomAsync(ConversationService store, string tenant, params string[] bodies)
    {
        var conversation = (await store.OpenAsync(Member("opener"), tenant, new ConversationSubject("booking", "B-" + Guid.NewGuid().ToString("N")))).ConversationId!.Value;
        await store.JoinAsync(Member("alice"), tenant, conversation, "Alice");
        var i = 0;
        foreach (var body in bodies)
        {
            await store.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m" + i++, body));
        }

        return conversation;
    }

    private async Task<string> AuditTableAsync()
    {
        var table = "oversight_audit_" + Guid.NewGuid().ToString("N")[..12];
        await fixture.ExecuteAsync($"CREATE TABLE {table} (id VARCHAR(40) NOT NULL);");
        return table;
    }

    private Task SetRetentionAsync(Guid conversation, int state) =>
        fixture.ExecuteAsync("UPDATE conversations SET retention_state = @State WHERE id = @Id;", new { State = state, Id = conversation });

    [Fact]
    public async Task Read_ShouldReturnThePageAndRecordIdsAndACount_WhenAStaffMemberReadsARoomTheyAreNotIn()
    {
        var tenant = NewTenant();
        var store = Store(masker: new TaggingMasker());
        var room = await RoomAsync(store, tenant, "one", "two", "three");
        var audit = new SpyAudit();
        var now = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

        var page = await Oversight(store, audit, new FixedTime(now)).ReadAsync(Member("staff"), tenant, room, 0, 50);

        Assert.True(page.Listed);
        Assert.Equal(["[Member]one", "[Member]two", "[Member]three"], page.Messages.Select(m => m.Body));
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(new OversightRead(tenant, room, ConversationActorKind.Member, "staff", 3, now), entry);
    }

    [Fact]
    public async Task Read_ShouldNotMakeTheReaderAParticipantOrMoveAMarker()
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one", "two");
        var before = await fixture.ScalarAsync<long>("SELECT COALESCE(SUM(last_read_seq), 0) FROM conversation_participants WHERE conversation_id = @Id;", new { Id = room });

        await Oversight(store, new SpyAudit()).ReadAsync(Member("staff"), tenant, room, 0, 50);

        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = @Id;", new { Id = room }));
        Assert.Equal(before, await fixture.ScalarAsync<long>("SELECT COALESCE(SUM(last_read_seq), 0) FROM conversation_participants WHERE conversation_id = @Id;", new { Id = room }));
    }

    [Fact]
    public async Task Read_ShouldBeRefusedAndNotAudited_WhenThePolicyDenies()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(Store(), tenant, "one");
        var audit = new SpyAudit();

        var page = await Oversight(Store(new DenyOversight()), audit).ReadAsync(Member("staff"), tenant, room, 0, 50);

        Assert.Equal(OversightReadOutcome.Refused, page.Outcome);
        Assert.Empty(page.Messages);
        Assert.Empty(audit.Entries);
    }

    [Theory]
    [InlineData(ConversationActorKind.Partner)]
    [InlineData(ConversationActorKind.System)]
    [InlineData(ConversationActorKind.Guest)]
    public async Task Read_ShouldBeRefusedAndNotAudited_WhenTheActorIsNotAMemberWithAUserReference(ConversationActorKind kind)
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        var audit = new SpyAudit();
        var actor = kind == ConversationActorKind.Guest ? ConversationActor.ForGuest(GuestSessionKey.Generate()) : new ConversationActor(kind, kind == ConversationActorKind.Partner ? "agent" : null);

        var page = await Oversight(store, audit).ReadAsync(actor, tenant, room, 0, 50);

        Assert.Equal(OversightReadOutcome.Refused, page.Outcome);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Read_ShouldBeRefused_WhenAMemberHasNoUserReference()
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        var audit = new SpyAudit();

        var page = await Oversight(store, audit).ReadAsync(new ConversationActor(ConversationActorKind.Member), tenant, room, 0, 50);

        Assert.Equal(OversightReadOutcome.Refused, page.Outcome);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Read_ShouldAnswerAuditPortMissingForAnyId_WhenNoAuditIsRegistered()
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        var oversight = Oversight(store, null);

        var existing = await oversight.ReadAsync(Member("staff"), tenant, room, 0, 50);
        var unknown = await oversight.ReadAsync(Member("staff"), tenant, Guid.NewGuid(), 0, 50);
        var otherTenant = await oversight.ReadAsync(Member("staff"), NewTenant(), room, 0, 50);

        Assert.Equal(OversightReadOutcome.AuditPortMissing, existing.Outcome);
        Assert.Equal(OversightReadOutcome.AuditPortMissing, unknown.Outcome);
        Assert.Equal(OversightReadOutcome.AuditPortMissing, otherTenant.Outcome);
        Assert.Empty(existing.Messages);
    }

    [Fact]
    public async Task Read_ShouldBeRefusedAndNotAudited_WhenTheConversationIsUnknownOrAnotherTenants()
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        var audit = new SpyAudit();
        var oversight = Oversight(store, audit);

        Assert.Equal(OversightReadOutcome.Refused, (await oversight.ReadAsync(Member("staff"), tenant, Guid.NewGuid(), 0, 50)).Outcome);
        Assert.Equal(OversightReadOutcome.Refused, (await oversight.ReadAsync(Member("staff"), NewTenant(), room, 0, 50)).Outcome);
        Assert.Empty(audit.Entries);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task Read_ShouldBeReadableInEveryRetentionStateButPurging(int retentionState, bool readable)
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        await SetRetentionAsync(room, retentionState);
        var audit = new SpyAudit();

        var page = await Oversight(store, audit).ReadAsync(Member("staff"), tenant, room, 0, 50);

        Assert.Equal(readable ? OversightReadOutcome.Listed : OversightReadOutcome.Refused, page.Outcome);
        Assert.Equal(readable ? 1 : 0, audit.Entries.Count);
    }

    [Fact]
    public async Task Read_ShouldAuditAnEmptyPageWithCountZero()
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        var audit = new SpyAudit();

        var page = await Oversight(store, audit).ReadAsync(Member("staff"), tenant, room, 99, 50);

        Assert.True(page.Listed);
        Assert.Empty(page.Messages);
        Assert.Equal(0, Assert.Single(audit.Entries).Count);
    }

    [Fact]
    public async Task Read_ShouldCommitTheAuditRowWithTheRead()
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        var table = await AuditTableAsync();

        var page = await Oversight(store, new SpyAudit(table)).ReadAsync(Member("staff"), tenant, room, 0, 50);

        Assert.True(page.Listed);
        Assert.Equal(1, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM {table};"));
    }

    [Fact]
    public async Task Read_ShouldReturnNothingAndRollBackTheAuditRow_WhenTheAuditPortThrows()
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        var table = await AuditTableAsync();
        var audit = new SpyAudit(table) { Fail = new InvalidOperationException("audit store down") };

        var page = await Oversight(store, audit).ReadAsync(Member("staff"), tenant, room, 0, 50);

        Assert.Equal(OversightReadOutcome.AuditFailed, page.Outcome);
        Assert.Empty(page.Messages);
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM {table};"));
    }

    [Fact]
    public async Task Read_ShouldAnswerAuditFailed_WhenThePortSwallowedASqlErrorThatAbortedTheTransaction()
    {
        if (fixture is not PostgresConversationFixture)
        {
            return; // only PostgreSQL aborts the whole transaction on a failed statement
        }

        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        var table = await AuditTableAsync();

        var page = await Oversight(store, new SpyAudit(table) { SwallowSqlError = true }).ReadAsync(Member("staff"), tenant, room, 0, 50);

        Assert.Equal(OversightReadOutcome.AuditFailed, page.Outcome);
        Assert.Empty(page.Messages);
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM {table};"));
    }

    [Fact]
    public async Task Read_ShouldRethrowCancellation_WhenTheAuditPortIsCancelled()
    {
        var tenant = NewTenant();
        var store = Store();
        var room = await RoomAsync(store, tenant, "one");
        var audit = new SpyAudit { Fail = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(() => Oversight(store, audit).ReadAsync(Member("staff"), tenant, room, 0, 50));
    }

    [Fact]
    public async Task Read_ShouldHideTheBodyButStillAudit_WhenTheMaskerThrows()
    {
        var tenant = NewTenant();
        var store = Store(masker: new TaggingMasker());
        var room = await RoomAsync(store, tenant, "fine", "boom");
        var audit = new SpyAudit();

        var page = await Oversight(store, audit).ReadAsync(Member("staff"), tenant, room, 0, 50);

        Assert.True(page.Listed);
        Assert.Equal("[Member]fine", page.Messages[0].Body);
        Assert.NotEqual("boom", page.Messages[1].Body);
        Assert.Equal(2, Assert.Single(audit.Entries).Count);
    }

    [Fact]
    public async Task Read_ShouldKeepARedactedBodyRedacted()
    {
        var tenant = NewTenant();
        var store = Store(masker: new TaggingMasker());
        var room = await RoomAsync(store, tenant, "gone", "kept");
        await fixture.ExecuteAsync("UPDATE conversation_messages SET body = NULL, redacted_at = @Now WHERE conversation_id = @Id AND seq = 1;", new { Id = room, Now = DateTime.UtcNow });

        var page = await Oversight(store, new SpyAudit()).ReadAsync(Member("staff"), tenant, room, 0, 50);

        Assert.Null(page.Messages[0].Body);
        Assert.True(page.Messages[0].IsRedacted);
        Assert.Equal("[Member]kept", page.Messages[1].Body);
    }

    [Fact]
    public async Task DefaultRegistration_ShouldResolveOversight_AndDenyItWithTheDefaultPolicy()
    {
        var oversight = fixture.Services.GetRequiredService<IConversationOversight>();

        var page = await oversight.ReadAsync(Member("staff"), NewTenant(), Guid.NewGuid(), 0, 10);

        Assert.Equal(OversightReadOutcome.Refused, page.Outcome);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationOversightTests(PostgresConversationFixture fixture) : ConversationOversightTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationOversightTests(MySqlConversationFixture fixture) : ConversationOversightTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationOversightTests(SqlServerConversationFixture fixture) : ConversationOversightTests(fixture);
