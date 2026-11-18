using Themia.Conversations.Internal;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>Listing a person's attachments for a data-subject access request (coord #0229): the same set per-person erasure deletes, metadata only.</summary>
public abstract partial class ConversationErasureTests
{
    private sealed class DenyListing : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(action != ConversationAction.ListPersonData);
    }

    private async Task<Guid> AttachAsync(string tenant, Guid conversation, Guid message, Guid owner, string key, string fileName = "")
    {
        var id = Guid.NewGuid();
        await fixture.ExecuteAsync(
            "INSERT INTO conversation_attachments (id, tenant_id, conversation_id, message_id, owner_participant_id, storage_key, content_type, size, sha256, created_at, file_name) VALUES (@Id, @Tenant, @Conversation, @Message, @Owner, @Key, 'image/png', 3, @Sha, @Now, @FileName);",
            new { Id = id, Tenant = tenant, Conversation = conversation, Message = message, Owner = owner, Key = key, Sha = new string('b', 64), Now = DateTime.UtcNow, FileName = fileName });
        return id;
    }

    private Task<Guid> MessageOfAsync(Guid conversation, Guid participant) =>
        fixture.ScalarAsync<Guid>("SELECT id FROM conversation_messages WHERE conversation_id = @C AND participant_id = @P ORDER BY seq;", new { C = conversation, P = participant });

    private async Task<HashSet<Guid>> AttachmentIdsAsync(Guid conversation)
    {
        var ids = new HashSet<Guid>();
        foreach (var raw in await fixture.QueryAsync<Guid>("SELECT id FROM conversation_attachments WHERE conversation_id = @C;", new { C = conversation }))
        {
            ids.Add(raw);
        }

        return ids;
    }

    [Fact]
    public async Task ListPersonAttachments_ShouldMatchExactlyTheRowsErasureDeletes_OnBothArmsOfThePredicate()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var ownedOnBobsMessage = await AttachAsync(tenant, room.Conversation, await MessageOfAsync(room.Conversation, room.Bob), room.Alice, "k/alice-on-bob");
        var bobsOnAlicesMessage = await AttachAsync(tenant, room.Conversation, room.AliceFirstMessage, room.Bob, "k/bob-on-alice");
        var bobsOwn = await AttachAsync(tenant, room.Conversation, await MessageOfAsync(room.Conversation, room.Bob), room.Bob, "k/bob-own");
        var before = await AttachmentIdsAsync(room.Conversation);

        var listed = await Subject().ListPersonAttachmentsAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));
        await Subject().ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));
        var deleted = before.Except(await AttachmentIdsAsync(room.Conversation)).ToHashSet();

        Assert.Equal(PersonAttachmentsOutcome.Listed, listed.Outcome);
        Assert.Equal(deleted, listed.Attachments.Select(a => a.AttachmentId).ToHashSet());
        Assert.Contains(ownedOnBobsMessage, deleted);
        Assert.Contains(bobsOnAlicesMessage, deleted);
        Assert.DoesNotContain(bobsOwn, deleted);
        Assert.Equal(3, listed.Attachments.Count);
    }

    [Fact]
    public async Task ListPersonAttachments_ShouldCarryMetadataInConversationAndSequenceOrder_WithoutTouchingAnything()
    {
        var tenant = NewTenant();
        var room = await RoomAsync(tenant);
        var second = await MessageOfAsync(room.Conversation, room.Bob);
        await AttachAsync(tenant, room.Conversation, second, room.Alice, "k/named", "contract.pdf");
        var storage = new RecordingStorage();
        var sink = new RecordingLifecycleSink();

        var listed = await Subject(storage, sink).ListPersonAttachmentsAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(2, listed.Attachments.Count);
        var first = listed.Attachments[0];
        Assert.Equal(room.Conversation, first.ConversationId);
        Assert.Equal(1, first.MessageSeq);
        Assert.Equal(room.AliceKey, first.StorageKey);
        Assert.Equal("image/png", first.ContentType);
        Assert.Equal(3, first.Size);
        Assert.Equal(new string('a', 64), first.Sha256);
        Assert.Equal(string.Empty, first.FileName);
        Assert.Equal(2, listed.Attachments[1].MessageSeq);
        Assert.Equal("contract.pdf", listed.Attachments[1].FileName);
        Assert.Empty(storage.Deleted);
        Assert.Empty(sink.Seen);
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM conversation_attachments WHERE conversation_id = @Id;", new { Id = room.Conversation }));
    }

    [Fact]
    public async Task ListPersonAttachments_ShouldListAHeldConversation_WhichErasureDefers()
    {
        var tenant = NewTenant();
        var held = await RoomAsync(tenant, "B-held");
        await fixture.ExecuteAsync("UPDATE conversations SET retention_state = 1 WHERE id = @Id;", new { Id = held.Conversation });

        var listed = await Subject().ListPersonAttachmentsAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));
        var erased = await Subject().ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Single(listed.Attachments);
        Assert.Equal(ErasureOutcome.Deferred, erased.Outcome);
    }

    [Fact]
    public async Task ListPersonAttachments_ShouldNotListAnotherTenantsPerson()
    {
        var tenant = NewTenant();
        await RoomAsync(tenant);

        var listed = await Subject().ListPersonAttachmentsAsync(Member("staff"), NewTenant(), PersonRef.ForMember("alice"));

        Assert.Equal(PersonAttachmentsOutcome.Listed, listed.Outcome);
        Assert.Empty(listed.Attachments);
    }

    [Fact]
    public async Task ListPersonAttachments_ShouldReportListedWithNoItems_ForAnAlreadyErasedPerson()
    {
        var tenant = NewTenant();
        await RoomAsync(tenant);
        await Subject().ErasePersonAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        var listed = await Subject().ListPersonAttachmentsAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(PersonAttachmentsOutcome.Listed, listed.Outcome);
        Assert.Empty(listed.Attachments);
    }

    [Fact]
    public async Task ListPersonAttachments_ShouldBeRefused_WhenThePolicyDeniesListPersonData_NotListedAsEmpty()
    {
        var tenant = NewTenant();
        await RoomAsync(tenant);

        var listed = await Subject(policy: new DenyListing()).ListPersonAttachmentsAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(PersonAttachmentsOutcome.Refused, listed.Outcome);
        Assert.False(listed.Listed);
        Assert.Empty(listed.Attachments);
    }

    [Fact]
    public async Task ListPersonAttachments_ShouldNotBeGrantedByEraseOrExport_Alone()
    {
        var tenant = NewTenant();
        await RoomAsync(tenant);

        var listed = await Subject(policy: new OnlyActions(ConversationAction.Erase, ConversationAction.Export)).ListPersonAttachmentsAsync(Member("staff"), tenant, PersonRef.ForMember("alice"));

        Assert.Equal(PersonAttachmentsOutcome.Refused, listed.Outcome);
    }

    [Fact]
    public async Task ListPersonAttachments_ShouldBeInvalid_ForABlankReference_AndForAGuestWithoutGuestAccess()
    {
        var tenant = NewTenant();
        await RoomAsync(tenant);

        var blank = await Subject().ListPersonAttachmentsAsync(Member("staff"), tenant, PersonRef.ForMember(" "));
        var guest = await Subject(guests: false).ListPersonAttachmentsAsync(Member("staff"), tenant, PersonRef.ForGuest(new GuestContact(GuestContactKind.Email, "g@example.test")));

        Assert.Equal(PersonAttachmentsOutcome.Invalid, blank.Outcome);
        Assert.Equal(PersonAttachmentsOutcome.Invalid, guest.Outcome);
        Assert.Empty(blank.Attachments);
    }

    private sealed class OnlyActions(params ConversationAction[] allowed) : IConversationAccessPolicy
    {
        public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(allowed.Contains(action));
    }
}
