using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Themia.Conversations.Internal;
using Themia.Imaging;
using Themia.Storage;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>Attaching files to a post and downloading them (coord #0182) on a real PostgreSQL, MySQL and SQL Server.</summary>
public abstract class ConversationAttachmentTests(ConversationEngineFixture fixture)
{
    private const int MiB = 1024 * 1024;

    private sealed class AllowAll : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class MemoryStorage : IStorageProvider
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

        public ConcurrentDictionary<string, string> Types { get; } = new();

        public List<PresignedUrlRequest> Presigned { get; } = [];

        public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            Objects[key] = buffer.ToArray();
            Types[key] = options.ContentType;
            return Task.FromResult(new StorageObjectInfo(key, buffer.Length, options.ContentType, null));
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Objects.ContainsKey(key));

        public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default)
        {
            Presigned.Add(request);
            return Task.FromResult(new Uri("https://storage.test/" + key));
        }

        public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Uri GetPublicUrl(string key) => throw new NotSupportedException();
    }

    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N");

    private static ConversationActor Member(string userRef) => new(ConversationActorKind.Member, userRef);

    private static readonly IImageProcessor Images = new SkiaImageProcessor(Options.Create(new ImageProcessingOptions()));

    private ConversationService Service(MemoryStorage storage, ConversationOptions? options = null, IImageProcessor? images = null) =>
        new(fixture.Dialect, options ?? new ConversationOptions(), new AllowAll(), new DefaultConversationEventSink(TimeProvider.System), new RegexConversationMasker(), TimeProvider.System, NullLogger<ConversationService>.Instance, storage, images ?? Images);

    private async Task<(string Tenant, Guid Conversation, Guid Alice)> RoomAsync(ConversationService service)
    {
        var tenant = NewTenant();
        var conversation = (await service.OpenAsync(Member("opener"), tenant, new ConversationSubject("booking", "B-1"))).ConversationId!.Value;
        var alice = (await service.JoinAsync(Member("alice"), tenant, conversation, "Alice")).ParticipantId!.Value;
        await service.JoinAsync(Member("bob"), tenant, conversation, "Bob");
        return (tenant, conversation, alice);
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A JPEG with an Exif APP1 segment carrying a recognisable marker, spliced in after the SOI.</summary>
    private static byte[] JpegWithExif()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.OrangeRed);
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 90);
        var jpeg = data.ToArray();
        var payload = Encoding.ASCII.GetBytes("Exif\0\0GPS-SECRET-MARKER");
        var length = payload.Length + 2;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    private static byte[] Pdf(int size) => [.. "%PDF-1.7\n"u8.ToArray(), .. new byte[size - 9]];

    private static AttachmentUpload Upload(string name, string type, byte[] bytes) => new(name, type, new MemoryStream(bytes));

    private Task<int> CountAsync(string table, Guid conversation) =>
        fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE conversation_id = @Id;", new { Id = conversation });

    [Fact]
    public async Task Post_ShouldStoreTheFile_AttachItToTheMessage_AndListIt()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);

        var result = await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", ""), [Upload("../../plan.pdf", "application/pdf", Pdf(2000))]);

        Assert.Equal(PostOutcome.Posted, result.Outcome);
        var page = await service.ListAsync(Member("bob"), tenant, conversation, 0, 10);
        var attachment = Assert.Single(Assert.Single(page.Messages).Attachments);
        Assert.Equal("plan.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(2000, attachment.Size);
        Assert.Single(storage.Objects);
        Assert.Equal(0, await CountAsync("conversation_attachment_uploads", conversation));
    }

    [Fact]
    public async Task Post_ShouldReEncodeAnImage_SoTheExifMarkerNeverReachesStorage()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        var original = JpegWithExif();
        Assert.Contains("GPS-SECRET-MARKER", Encoding.ASCII.GetString(original));

        var result = await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "photo"), [Upload("p.jpg", "image/jpeg", original)]);

        Assert.Equal(PostOutcome.Posted, result.Outcome);
        var stored = Assert.Single(storage.Objects.Values);
        Assert.DoesNotContain("GPS-SECRET-MARKER", Encoding.ASCII.GetString(stored));
        Assert.Equal("image/webp", Assert.Single(storage.Types.Values));
    }

    [Fact]
    public async Task Post_ShouldRejectAFileWhoseBytesDoNotMatchTheDeclaredType_AndWriteNothing()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);

        var result = await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("p.png", "image/png", Pdf(500))]);

        Assert.Equal(PostOutcome.AttachmentRejected, result.Outcome);
        Assert.Empty(storage.Objects);
        Assert.Equal(0, await CountAsync("conversation_attachment_uploads", conversation));
        Assert.Equal(0, await CountAsync("conversation_messages", conversation));
    }

    [Fact]
    public async Task Post_ShouldRejectAnImageDeclaredAsPdf_BecauseTheStoredBytesWouldBeServedUnchecked()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, _) = await RoomAsync(service);

        var result = await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("doc.pdf", "application/pdf", Png())]);

        Assert.Equal(PostOutcome.AttachmentRejected, result.Outcome);
        Assert.Empty(storage.Objects);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    public async Task Post_ShouldRejectATypeOutsideTheAllowList(string type)
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);

        var result = await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("p", type, "<svg/>"u8.ToArray())]);

        Assert.Equal(PostOutcome.AttachmentRejected, result.Outcome);
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task Post_ShouldRejectACorruptImage_WithoutStoringTheRawBytes()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        byte[] corrupt = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

        var result = await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("p.png", "image/png", corrupt)]);

        Assert.Equal(PostOutcome.AttachmentRejected, result.Outcome);
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task Post_ShouldLeaveNoObject_WhenTheCallerIsNotAParticipant()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);

        var result = await service.PostAsync(Member("stranger"), tenant, conversation, new PostMessage("m1", "x"), [Upload("p.pdf", "application/pdf", Pdf(500))]);

        Assert.Equal(PostOutcome.Refused, result.Outcome);
        Assert.Empty(storage.Objects);
        Assert.Equal(0, await CountAsync("conversation_attachment_uploads", conversation));
    }

    [Fact]
    public async Task Post_ShouldLeaveNoObject_WhenTheConversationIsClosed()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        await service.CloseAsync(Member("alice"), tenant, conversation);

        var result = await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("p.pdf", "application/pdf", Pdf(500))]);

        Assert.NotEqual(PostOutcome.Posted, result.Outcome);
        Assert.Empty(storage.Objects);
        Assert.Equal(0, await CountAsync("conversation_attachment_uploads", conversation));
        Assert.Equal(0, await CountAsync("conversation_attachments", conversation));
    }

    [Fact]
    public async Task Post_ShouldReturnTheStoredMessage_AndWriteNoSecondSetOfObjects_WhenTheClientMessageIdRepeats()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        var first = await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("same", "x"), [Upload("a.pdf", "application/pdf", Pdf(600))]);

        var second = await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("same", "x"), [Upload("a.pdf", "application/pdf", Pdf(600))]);

        Assert.Equal(PostOutcome.Posted, first.Outcome);
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Single(storage.Objects);
        Assert.Equal(1, await CountAsync("conversation_attachments", conversation));
        Assert.Equal(0, await CountAsync("conversation_attachment_uploads", conversation));
    }

    [Fact]
    public async Task Post_ShouldNeverExceedTheConversationQuota_WhenPostsRaceOverTheCap()
    {
        var storage = new MemoryStorage();
        var options = new ConversationOptions { MaxAttachmentBytesPerMessage = 10 * MiB, MaxAttachmentBytesPerConversation = 10 * MiB };
        var service = Service(storage, options);
        var (tenant, conversation, alice) = await RoomAsync(service);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            service.PostAsync(Member("alice"), tenant, conversation, new PostMessage($"m{i}", "x"), [Upload("a.pdf", "application/pdf", Pdf(4 * MiB))])));

        Assert.Equal(2, results.Count(r => r.Outcome == PostOutcome.Posted));
        Assert.Equal(3, results.Count(r => r.Outcome == PostOutcome.QuotaExceeded));
        Assert.Equal(2, storage.Objects.Count);
        Assert.Equal(8L * MiB, await fixture.ScalarAsync<long>("SELECT SUM(size) FROM conversation_attachments WHERE conversation_id = @Id;", new { Id = conversation }));
        Assert.Equal(2, await CountAsync("conversation_messages", conversation));
        Assert.Equal(0, await CountAsync("conversation_attachment_uploads", conversation));
    }

    [Fact]
    public async Task Open_ShouldReturnAShortLivedAttachmentUrl_ToAParticipant()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("plan.pdf", "application/pdf", Pdf(700))]);
        var id = (await service.ListAsync(Member("bob"), tenant, conversation, 0, 10)).Messages[0].Attachments[0].Id;

        var result = await service.OpenAsync(Member("bob"), tenant, conversation, id);

        Assert.True(result.Succeeded);
        Assert.Equal("plan.pdf", result.Attachment!.FileName);
        var request = Assert.Single(storage.Presigned);
        Assert.Equal(TimeSpan.FromSeconds(60), request.Expiry);
        Assert.StartsWith("attachment;", request.ContentDisposition);
    }

    [Fact]
    public async Task Open_ShouldRefuse_ANonParticipant_AnotherTenant_AndAnUnknownId()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("plan.pdf", "application/pdf", Pdf(700))]);
        var id = (await service.ListAsync(Member("bob"), tenant, conversation, 0, 10)).Messages[0].Attachments[0].Id;

        Assert.False((await service.OpenAsync(Member("stranger"), tenant, conversation, id)).Succeeded);
        Assert.False((await service.OpenAsync(Member("bob"), NewTenant(), conversation, id)).Succeeded);
        Assert.False((await service.OpenAsync(Member("bob"), tenant, conversation, Guid.NewGuid())).Succeeded);
        Assert.Empty(storage.Presigned);
    }

    [Fact]
    public async Task Open_ShouldRefuse_AnAttachmentOfAnotherConversation()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("plan.pdf", "application/pdf", Pdf(700))]);
        var id = (await service.ListAsync(Member("bob"), tenant, conversation, 0, 10)).Messages[0].Attachments[0].Id;
        var other = (await service.OpenAsync(Member("bob"), tenant, new ConversationSubject("booking", "B-2"))).ConversationId!.Value;
        await service.JoinAsync(Member("bob"), tenant, other, "Bob");

        Assert.False((await service.OpenAsync(Member("bob"), tenant, other, id)).Succeeded);
    }

    [Fact]
    public async Task Erase_ShouldDeleteThePersonsFile_FromStorage_AndRefuseTheDownload()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("plan.pdf", "application/pdf", Pdf(700))]);
        var id = (await service.ListAsync(Member("bob"), tenant, conversation, 0, 10)).Messages[0].Attachments[0].Id;
        var stagedKey = $"conversations/{tenant}/{conversation:N}/leaked";
        storage.Objects[stagedKey] = [1];
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_attachment_uploads (id, tenant_id, conversation_id, owner_participant_id, storage_key, created_at) VALUES (@Id, @Tenant, @Conversation, @Owner, @Key, @Now);",
            new { Id = Guid.NewGuid(), Tenant = tenant, Conversation = conversation, Owner = alice, Key = stagedKey, Now = DateTime.UtcNow });
        var subject = new ConversationDataSubjectService(fixture.Dialect, new AllowAll(), new DefaultConversationLifecycleSink(), null, storage, TimeProvider.System, NullLogger<ConversationDataSubjectService>.Instance);

        var erased = await subject.ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(ErasureOutcome.Erased, erased.Outcome);
        Assert.Empty(storage.Objects);
        Assert.Equal(0, await CountAsync("conversation_attachment_uploads", conversation));
        Assert.False((await service.OpenAsync(Member("bob"), tenant, conversation, id)).Succeeded);
    }

    [Fact]
    public async Task Sweep_ShouldDeleteOnlyStaleStagedUploads()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        var (stale, fresh) = ($"conversations/{tenant}/stale", $"conversations/{tenant}/fresh");
        storage.Objects[stale] = [1];
        storage.Objects[fresh] = [1];
        foreach (var (key, age) in new[] { (stale, TimeSpan.FromHours(3)), (fresh, TimeSpan.FromMinutes(5)) })
        {
            await fixture.ExecuteAsync(
                "INSERT INTO conversation_attachment_uploads (id, tenant_id, conversation_id, owner_participant_id, storage_key, created_at) VALUES (@Id, @Tenant, @Conversation, @Owner, @Key, @Created);",
                new { Id = Guid.NewGuid(), Tenant = tenant, Conversation = conversation, Owner = alice, Key = key, Created = DateTime.UtcNow - age });
        }

        var retention = new ConversationRetentionService(fixture.Dialect, new ConversationOptions { NoLegalHoldPort = true }, new AllowAll(), new DefaultConversationLifecycleSink(), null, storage, TimeProvider.System, NullLogger<ConversationRetentionService>.Instance);
        await retention.SweepAsync(tenant);

        Assert.False(storage.Objects.ContainsKey(stale));
        Assert.True(storage.Objects.ContainsKey(fresh));
        Assert.Equal(1, await CountAsync("conversation_attachment_uploads", conversation));
    }

    [Fact]
    public async Task Purge_ShouldDeleteTheStagedKeysOfTheConversation()
    {
        var storage = new MemoryStorage();
        var service = Service(storage);
        var (tenant, conversation, alice) = await RoomAsync(service);
        await service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("plan.pdf", "application/pdf", Pdf(700))]);
        var staged = $"conversations/{tenant}/{conversation:N}/staged";
        storage.Objects[staged] = [1];
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_attachment_uploads (id, tenant_id, conversation_id, owner_participant_id, storage_key, created_at) VALUES (@Id, @Tenant, @Conversation, @Owner, @Key, @Now);",
            new { Id = Guid.NewGuid(), Tenant = tenant, Conversation = conversation, Owner = alice, Key = staged, Now = DateTime.UtcNow });
        await service.CloseAsync(Member("alice"), tenant, conversation);
        await fixture.ExecuteAsync("UPDATE conversations SET retention_until = @Until WHERE id = @Id;", new { Id = conversation, Until = DateTime.UtcNow.AddDays(-1) });

        var retention = new ConversationRetentionService(fixture.Dialect, new ConversationOptions { NoLegalHoldPort = true }, new AllowAll(), new DefaultConversationLifecycleSink(), null, storage, TimeProvider.System, NullLogger<ConversationRetentionService>.Instance);
        var result = await retention.SweepAsync(tenant);

        Assert.Equal(1, result.Purged);
        Assert.Empty(storage.Objects);
        Assert.Equal(0, await CountAsync("conversation_attachment_uploads", conversation));
    }

    [Fact]
    public async Task Post_ShouldThrow_WhenNoImageProcessorOrStorageIsRegistered()
    {
        var service = new ConversationService(fixture.Dialect, new ConversationOptions(), new AllowAll(), new DefaultConversationEventSink(TimeProvider.System), new RegexConversationMasker(), TimeProvider.System, NullLogger<ConversationService>.Instance);
        var (tenant, conversation, alice) = await RoomAsync(service);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PostAsync(Member("alice"), tenant, conversation, new PostMessage("m1", "x"), [Upload("p.pdf", "application/pdf", Pdf(500))]));
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationAttachmentTests(PostgresConversationFixture fixture) : ConversationAttachmentTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationAttachmentTests(MySqlConversationFixture fixture) : ConversationAttachmentTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationAttachmentTests(SqlServerConversationFixture fixture) : ConversationAttachmentTests(fixture);
