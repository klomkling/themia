using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Themia.Conversations.Internal;
using Themia.Imaging;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>Staff oversight open of a file (coord #0228) and its audit on a real PostgreSQL, MySQL and SQL Server.</summary>
public abstract class ConversationOversightAttachmentTests(ConversationEngineFixture fixture)
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

    /// <summary>Records every entry; can fail, and can swallow a SQL error like a port that dedupes on a unique violation.</summary>
    private sealed class SpyAudit : IConversationReadAudit
    {
        public List<OversightRead> Entries { get; } = [];

        public Exception? Fail { get; set; }

        public bool SwallowSqlError { get; set; }

        public async Task RecordAsync(DbConnection connection, DbTransaction transaction, OversightRead entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            if (SwallowSqlError)
            {
                try
                {
                    await Dapper.SqlMapper.ExecuteAsync(connection, new Dapper.CommandDefinition("SELECT * FROM table_that_does_not_exist_oversight_open;", transaction: transaction, cancellationToken: cancellationToken));
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

    private static readonly IImageProcessor Images = new SkiaImageProcessor(Options.Create(new ImageProcessingOptions()));

    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N");

    private static ConversationActor Member(string userRef) => new(ConversationActorKind.Member, userRef);

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private ConversationService Store(ConversationAttachmentTests.MemoryStorage storage, IConversationAccessPolicy? policy = null) =>
        new(fixture.Dialect, new ConversationOptions(), policy ?? new AllowAll(), new DefaultConversationEventSink(TimeProvider.System), new RegexConversationMasker(), TimeProvider.System, NullLogger<ConversationService>.Instance, storage, Images);

    private static ConversationOversightAttachmentService Oversight(ConversationService store, IConversationReadAudit? audit) =>
        new(store, audit, TimeProvider.System, NullLogger<ConversationOversightAttachmentService>.Instance);

    /// <summary>A room with one attachment posted by alice; returns the ids.</summary>
    private static async Task<(string Tenant, Guid Room, Guid Attachment)> RoomWithFileAsync(ConversationService store, string? tenant = null)
    {
        tenant ??= NewTenant();
        var room = (await store.OpenAsync(Member("opener"), tenant, new ConversationSubject("booking", "B-" + Guid.NewGuid().ToString("N")))).ConversationId!.Value;
        await store.JoinAsync(Member("alice"), tenant, room, "Alice");
        await store.PostAsync(Member("alice"), tenant, room, new PostMessage("m1", "see file"), [new AttachmentUpload("plan.png", "image/png", new MemoryStream(Png()))]);
        var page = await store.ListAsync(Member("alice"), tenant, room, 0, 10);
        return (tenant, room, Assert.Single(Assert.Single(page.Messages).Attachments).Id);
    }

    private Task SetRetentionAsync(Guid room, int state) =>
        fixture.ExecuteAsync("UPDATE conversations SET retention_state = @State WHERE id = @Id;", new { State = state, Id = room });

    [Fact]
    public async Task Open_ShouldReturnAUrlAndRecordTheAttachmentId_WhenAStaffMemberOpensAFileOfARoomTheyAreNotIn()
    {
        var storage = new ConversationAttachmentTests.MemoryStorage();
        var store = Store(storage);
        var (tenant, room, attachment) = await RoomWithFileAsync(store);
        var audit = new SpyAudit();

        var result = await Oversight(store, audit).OpenAsync(Member("staff"), tenant, room, attachment);

        Assert.True(result.Opened);
        Assert.NotNull(result.Url);
        Assert.Equal(attachment, result.Attachment!.Id);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal((tenant, room, "staff", 1, (Guid?)attachment), (entry.TenantId, entry.ConversationId, entry.ActorUserRef, entry.Count, entry.AttachmentId));
        Assert.Contains(storage.Presigned, p => p.ContentDisposition is not null && p.ContentDisposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Open_ShouldNotMakeTheOpenerAParticipant()
    {
        var store = Store(new ConversationAttachmentTests.MemoryStorage());
        var (tenant, room, attachment) = await RoomWithFileAsync(store);
        var before = await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = @Id;", new { Id = room });

        await Oversight(store, new SpyAudit()).OpenAsync(Member("staff"), tenant, room, attachment);

        Assert.Equal(before, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = @Id;", new { Id = room }));
    }

    [Fact]
    public async Task Open_ShouldBeRefusedAndNotAudited_WhenThePolicyDenies()
    {
        var storage = new ConversationAttachmentTests.MemoryStorage();
        var (tenant, room, attachment) = await RoomWithFileAsync(Store(storage));
        var audit = new SpyAudit();

        var result = await Oversight(Store(storage, new DenyOversight()), audit).OpenAsync(Member("staff"), tenant, room, attachment);

        Assert.Equal(OversightOpenOutcome.Refused, result.Outcome);
        Assert.Empty(audit.Entries);
        Assert.Empty(storage.Presigned);
    }

    [Theory]
    [InlineData(ConversationActorKind.Guest)]
    [InlineData(ConversationActorKind.System)]
    public async Task Open_ShouldBeRefused_WhenTheActorIsNotAMember(ConversationActorKind kind)
    {
        var storage = new ConversationAttachmentTests.MemoryStorage();
        var store = Store(storage);
        var (tenant, room, attachment) = await RoomWithFileAsync(store);
        var audit = new SpyAudit();

        var result = await Oversight(store, audit).OpenAsync(new ConversationActor(kind, "x"), tenant, room, attachment);

        Assert.Equal(OversightOpenOutcome.Refused, result.Outcome);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Open_ShouldBeRefused_WhenTheAttachmentBelongsToAnotherConversation()
    {
        var storage = new ConversationAttachmentTests.MemoryStorage();
        var store = Store(storage);
        var (tenant, room, _) = await RoomWithFileAsync(store);
        var (_, _, foreign) = await RoomWithFileAsync(store, tenant);
        var audit = new SpyAudit();

        var result = await Oversight(store, audit).OpenAsync(Member("staff"), tenant, room, foreign);

        Assert.Equal(OversightOpenOutcome.Refused, result.Outcome);
        Assert.Empty(audit.Entries);
        Assert.Empty(storage.Presigned);
    }

    [Fact]
    public async Task Open_ShouldBeRefused_WhenTheRoomBelongsToAnotherTenant()
    {
        var storage = new ConversationAttachmentTests.MemoryStorage();
        var store = Store(storage);
        var (_, room, attachment) = await RoomWithFileAsync(store);

        var result = await Oversight(store, new SpyAudit()).OpenAsync(Member("staff"), NewTenant(), room, attachment);

        Assert.Equal(OversightOpenOutcome.Refused, result.Outcome);
        Assert.Empty(storage.Presigned);
    }

    [Fact]
    public async Task Open_ShouldBeRefused_WhenTheRoomIsBeingPurged()
    {
        var storage = new ConversationAttachmentTests.MemoryStorage();
        var store = Store(storage);
        var (tenant, room, attachment) = await RoomWithFileAsync(store);
        await SetRetentionAsync(room, (int)ConversationRetentionState.Purging);
        var audit = new SpyAudit();

        var result = await Oversight(store, audit).OpenAsync(Member("staff"), tenant, room, attachment);

        Assert.Equal(OversightOpenOutcome.Refused, result.Outcome);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Open_ShouldStillOpen_WhenTheRoomIsPastRetention()
    {
        var storage = new ConversationAttachmentTests.MemoryStorage();
        var store = Store(storage);
        var (tenant, room, attachment) = await RoomWithFileAsync(store);
        await SetRetentionAsync(room, (int)ConversationRetentionState.Expired);
        Assert.NotEmpty(storage.Objects);

        var result = await Oversight(store, new SpyAudit()).OpenAsync(Member("staff"), tenant, room, attachment);

        Assert.True(result.Opened);
    }

    [Fact]
    public async Task Open_ShouldAnswerAuditPortMissing_WhenNoPortIsRegistered()
    {
        var storage = new ConversationAttachmentTests.MemoryStorage();
        var store = Store(storage);
        var (tenant, room, attachment) = await RoomWithFileAsync(store);

        var result = await Oversight(store, null).OpenAsync(Member("staff"), tenant, room, attachment);

        Assert.Equal(OversightOpenOutcome.AuditPortMissing, result.Outcome);
        Assert.Null(result.Url);
        Assert.Empty(storage.Presigned);
    }

    [Fact]
    public async Task Open_ShouldIssueNoUrl_WhenTheAuditPortThrows()
    {
        var storage = new ConversationAttachmentTests.MemoryStorage();
        var store = Store(storage);
        var (tenant, room, attachment) = await RoomWithFileAsync(store);
        var audit = new SpyAudit { Fail = new InvalidOperationException("audit down") };

        var result = await Oversight(store, audit).OpenAsync(Member("staff"), tenant, room, attachment);

        Assert.Equal(OversightOpenOutcome.AuditFailed, result.Outcome);
        Assert.Null(result.Url);
        Assert.Empty(storage.Presigned);
    }

    [Fact]
    public async Task Open_ShouldIssueNoUrl_WhenThePortSwallowedItsOwnSqlError()
    {
        if (fixture is not PostgresConversationFixture)
        {
            return; // only PostgreSQL aborts the whole transaction on a failed statement
        }

        var storage = new ConversationAttachmentTests.MemoryStorage();
        var store = Store(storage);
        var (tenant, room, attachment) = await RoomWithFileAsync(store);
        var audit = new SpyAudit { SwallowSqlError = true };

        var result = await Oversight(store, audit).OpenAsync(Member("staff"), tenant, room, attachment);

        Assert.Equal(OversightOpenOutcome.AuditFailed, result.Outcome);
        Assert.Empty(storage.Presigned);
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationOversightAttachmentTests(PostgresConversationFixture fixture) : ConversationOversightAttachmentTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationOversightAttachmentTests(MySqlConversationFixture fixture) : ConversationOversightAttachmentTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationOversightAttachmentTests(SqlServerConversationFixture fixture) : ConversationOversightAttachmentTests(fixture);
