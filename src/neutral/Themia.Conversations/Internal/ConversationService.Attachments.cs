using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Logging;
using Themia.Imaging;
using Themia.Storage;

namespace Themia.Conversations.Internal;

internal sealed partial class ConversationService
{
    /// <summary>A file that passed every check and is ready to be staged, written and attached.</summary>
    internal sealed record PreparedAttachment(Guid Id, string StorageKey, string FileName, string ContentType, byte[] Content, string Sha256);

    /// <inheritdoc />
    public async Task<PostResult> PostAsync(ConversationActor actor, string tenantId, Guid conversationId, PostMessage message, IReadOnlyList<AttachmentUpload> files, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(files);
        ConversationInput.ClientMessageId(message.ClientMessageId);
        var (storage, images) = RequireAttachmentSupport();

        if (!await AllowedAsync(actor, tenantId, conversationId, ConversationAction.Post, cancellationToken))
        {
            return new PostResult(PostOutcome.Refused, null, null);
        }

        var bodyBlank = string.IsNullOrWhiteSpace(message.Body);
        if (message.Body is { } body && body.Length > options.MaxBodyLength || bodyBlank && files.Count == 0)
        {
            return new PostResult(PostOutcome.InvalidMessage, null, null);
        }

        if (files.Count == 0)
        {
            return await PostAsync(actor, tenantId, conversationId, message, cancellationToken);
        }

        // Everything that can say no without touching storage runs first, so a refused or repeated post writes nothing.
        if (files.Count > options.MaxAttachmentsPerMessage)
        {
            return new PostResult(PostOutcome.AttachmentRejected, null, null);
        }

        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            if (await ResolveParticipantAsync(connection, null, actor, tenantId, conversationId, cancellationToken) is not { } participantId)
            {
                return new PostResult(PostOutcome.Refused, null, null);
            }

            if (await FindMessageAsync(connection, null, tenantId, conversationId, participantId, message.ClientMessageId, cancellationToken) is { } existing)
            {
                return existing;
            }
        }

        var prepared = await PrepareAsync(images, tenantId, conversationId, files, cancellationToken);
        if (prepared is null)
        {
            return new PostResult(PostOutcome.AttachmentRejected, null, null);
        }

        var total = prepared.Sum(p => (long)p.Content.Length);
        if (await StoredBytesAsync(tenantId, conversationId, cancellationToken) + total > options.MaxAttachmentBytesPerConversation)
        {
            return new PostResult(PostOutcome.QuotaExceeded, null, null);
        }

        var ownerForStaging = await OwnerOfAsync(actor, tenantId, conversationId, cancellationToken);
        if (ownerForStaging is not { } owner)
        {
            return new PostResult(PostOutcome.Refused, null, null);
        }

        try
        {
            await StageAndWriteAsync(storage, tenantId, conversationId, owner, prepared, cancellationToken);
            var result = await PostWithRetryAsync(actor, tenantId, conversationId, message, prepared, cancellationToken);
            if (result.Outcome != PostOutcome.Posted)
            {
                await DiscardAsync(storage, tenantId, prepared);
            }

            return result;
        }
        catch
        {
            await DiscardAsync(storage, tenantId, prepared); // CancellationToken.None inside: a cancelled caller must not leave the objects behind
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<OpenAttachmentResult> OpenAsync(ConversationActor actor, string tenantId, Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        ConversationInput.Actor(actor);
        ConversationInput.Tenant(tenantId, nameof(tenantId));
        var (storage, _) = RequireAttachmentSupport();
        var refused = new OpenAttachmentResult(OpenAttachmentOutcome.Refused, null, null);

        if (!await AllowedAsync(actor, tenantId, conversationId, ConversationAction.Read, cancellationToken))
        {
            return refused;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await ResolveParticipantAsync(connection, null, actor, tenantId, conversationId, cancellationToken) is null)
        {
            return refused;
        }

        var row = await connection.QuerySingleOrDefaultAsync<AttachmentRow>(new CommandDefinition(
            "SELECT id AS Id, storage_key AS StorageKey, file_name AS FileName, content_type AS ContentType, size AS Size FROM conversation_attachments " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND id = @AttachmentId;",
            new { TenantId = tenantId, ConversationId = conversationId, AttachmentId = attachmentId },
            cancellationToken: cancellationToken));
        if (row is null)
        {
            return refused;
        }

        var url = await storage.GetPresignedUrlAsync(
            row.StorageKey,
            new PresignedUrlRequest(PresignedUrlOperation.Get, options.AttachmentDownloadLifetime) { ContentDisposition = AttachmentFileName.ContentDisposition(row.FileName) },
            cancellationToken);
        return new OpenAttachmentResult(OpenAttachmentOutcome.Opened, url, row.ToInfo());
    }

    private (IStorageProvider Storage, IImageProcessor Images) RequireAttachmentSupport() =>
        storage is not null && images is not null
            ? (storage, images)
            : throw new InvalidOperationException("Attachments need an IStorageProvider and an IImageProcessor; register them and call AddThemiaConversationAttachments.");

    private async Task<PostResult> PostWithRetryAsync(ConversationActor actor, string tenantId, Guid conversationId, PostMessage message, IReadOnlyList<PreparedAttachment> prepared, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PostOnceAsync(actor, tenantId, conversationId, message, cancellationToken, prepared);
            }
            catch (DbException ex) when (attempt < MaxPostAttempts && dialect.IsDeadlock(ex))
            {
                logger.LogWarning("Conversation {ConversationId} post with attachments lost a lock race; retrying (attempt {Attempt})", conversationId, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>Validates, sniffs and (for images) re-encodes every file. <see langword="null"/> when any file is refused: the message then stores nothing.</summary>
    private async Task<List<PreparedAttachment>?> PrepareAsync(IImageProcessor images, string tenantId, Guid conversationId, IReadOnlyList<AttachmentUpload> files, CancellationToken cancellationToken)
    {
        var result = new List<PreparedAttachment>(files.Count);
        long total = 0;
        foreach (var file in files)
        {
            var declared = file.ContentType?.Trim().ToLowerInvariant();
            if (declared is null || !options.AllowedAttachmentContentTypes.Contains(declared, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            var bytes = await ReadBoundedAsync(file.Content, options.MaxAttachmentBytesPerMessage - total, cancellationToken);
            if (bytes is null || !AttachmentSniffer.Matches(declared, bytes))
            {
                return null;
            }

            var contentType = declared;
            if (AttachmentSniffer.IsImage(declared))
            {
                try
                {
                    using var processed = await images.ProcessAsync(new MemoryStream(bytes), null, cancellationToken);
                    using var buffer = new MemoryStream();
                    await processed.Content.CopyToAsync(buffer, cancellationToken);
                    bytes = buffer.ToArray();
                    contentType = AttachmentSniffer.ImageTypeOf(processed.Extension);
                }
                catch (ArgumentException)
                {
                    return null; // corrupt or unsupported image: refuse the message rather than store the raw bytes
                }
            }

            total += bytes.Length;
            if (total > options.MaxAttachmentBytesPerMessage)
            {
                return null;
            }

            var id = Guid.NewGuid();
            result.Add(new PreparedAttachment(id, $"conversations/{tenantId}/{conversationId:N}/{id:N}", AttachmentFileName.Sanitize(file.FileName), contentType, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        return result;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream content, long limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (limit <= 0)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>Commits a staging row for each key, then writes the object. The row exists before the object, so a crash in between can only leave a row without an object.</summary>
    private async Task StageAndWriteAsync(IStorageProvider storage, string tenantId, Guid conversationId, Guid owner, IReadOnlyList<PreparedAttachment> prepared, CancellationToken cancellationToken)
    {
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            foreach (var item in prepared)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO conversation_attachment_uploads (id, tenant_id, conversation_id, owner_participant_id, storage_key, created_at) VALUES (@Id, @TenantId, @ConversationId, @Owner, @StorageKey, @Now);",
                    new { item.Id, TenantId = tenantId, ConversationId = conversationId, Owner = owner, item.StorageKey, Now = Now() },
                    cancellationToken: cancellationToken));
            }
        }

        foreach (var item in prepared)
        {
            using var content = new MemoryStream(item.Content, writable: false);
            await storage.PutAsync(item.StorageKey, content, new StoragePutOptions(item.ContentType), cancellationToken);
        }
    }

    /// <summary>Best effort: deletes each object, then its staging row. A failed delete keeps the row, so the sweeper finds the key later. Never cancelled.</summary>
    private async Task DiscardAsync(IStorageProvider storage, string tenantId, IReadOnlyList<PreparedAttachment> prepared)
    {
        foreach (var item in prepared)
        {
            try
            {
                await storage.DeleteAsync(item.StorageKey, CancellationToken.None);
                await using var connection = await OpenConnectionAsync(CancellationToken.None);
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM conversation_attachment_uploads WHERE tenant_id = @TenantId AND id = @Id;",
                    new { TenantId = tenantId, item.Id },
                    cancellationToken: CancellationToken.None));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning("An unposted attachment {AttachmentId} could not be cleaned up ({ExceptionType}); the sweep will retry", item.Id, ex.GetType().Name);
            }
        }
    }

    private async Task<long> StoredBytesAsync(string tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await StoredBytesAsync(connection, null, tenantId, conversationId, cancellationToken);
    }

    private static async Task<long> StoredBytesAsync(DbConnection connection, DbTransaction? transaction, string tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(SUM(size), 0) FROM conversation_attachments WHERE tenant_id = @TenantId AND conversation_id = @ConversationId;",
            new { TenantId = tenantId, ConversationId = conversationId },
            transaction,
            cancellationToken: cancellationToken));

    private async Task<Guid?> OwnerOfAsync(ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ResolveParticipantAsync(connection, null, actor, tenantId, conversationId, cancellationToken);
    }

    /// <summary>Inside the post transaction, under the counter lock: re-check the quota, insert the rows, drop the staging rows.</summary>
    private async Task<bool> AttachAsync(DbConnection connection, DbTransaction transaction, string tenantId, Guid conversationId, Guid participantId, Guid messageId, IReadOnlyList<PreparedAttachment> prepared, CancellationToken cancellationToken)
    {
        var total = prepared.Sum(p => (long)p.Content.Length);
        if (await StoredBytesAsync(connection, transaction, tenantId, conversationId, cancellationToken) + total > options.MaxAttachmentBytesPerConversation)
        {
            return false;
        }

        foreach (var item in prepared)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO conversation_attachments (id, tenant_id, conversation_id, message_id, owner_participant_id, storage_key, file_name, content_type, size, sha256, created_at) " +
                "VALUES (@Id, @TenantId, @ConversationId, @MessageId, @Owner, @StorageKey, @FileName, @ContentType, @Size, @Sha256, @Now);",
                new { item.Id, TenantId = tenantId, ConversationId = conversationId, MessageId = messageId, Owner = participantId, item.StorageKey, item.FileName, item.ContentType, Size = (long)item.Content.Length, item.Sha256, Now = Now() },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM conversation_attachment_uploads WHERE tenant_id = @TenantId AND id = @Id;",
                new { TenantId = tenantId, item.Id },
                transaction,
                cancellationToken: cancellationToken));
        }

        return true;
    }

    /// <summary>The attachments of the messages with <paramref name="afterSeq"/> &lt; seq &lt;= <paramref name="untilSeq"/>, grouped by message id. A range, not an id list, so one statement works on every engine.</summary>
    private static async Task<Dictionary<Guid, List<AttachmentInfo>>> AttachmentsOfAsync(DbConnection connection, DbTransaction? transaction, string tenantId, Guid conversationId, long afterSeq, long untilSeq, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<AttachmentInfo>>();
        if (untilSeq <= afterSeq)
        {
            return result;
        }

        var rows = await connection.QueryAsync<AttachmentRow>(new CommandDefinition(
            "SELECT a.id AS Id, a.message_id AS MessageId, a.storage_key AS StorageKey, a.file_name AS FileName, a.content_type AS ContentType, a.size AS Size " +
            "FROM conversation_attachments a JOIN conversation_messages m ON m.tenant_id = a.tenant_id AND m.conversation_id = a.conversation_id AND m.id = a.message_id " +
            "WHERE a.tenant_id = @TenantId AND a.conversation_id = @ConversationId AND m.seq > @AfterSeq AND m.seq <= @UntilSeq ORDER BY a.created_at, a.id;",
            new { TenantId = tenantId, ConversationId = conversationId, AfterSeq = afterSeq, UntilSeq = untilSeq },
            transaction,
            cancellationToken: cancellationToken));
        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.MessageId, out var list))
            {
                result[row.MessageId] = list = [];
            }

            list.Add(row.ToInfo());
        }

        return result;
    }

    private sealed class AttachmentRow
    {
        public Guid Id { get; set; }

        public Guid MessageId { get; set; }

        public string StorageKey { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public string ContentType { get; set; } = string.Empty;

        public long Size { get; set; }

        public AttachmentInfo ToInfo() => new(Id, FileName, ContentType, Size);
    }
}
