using Themia.Conversations;

namespace Themia.Conversations.AspNetCore.Tests;

internal sealed record ServiceCall(string Method, ConversationActor Actor, string TenantId, Guid ConversationId, object? Argument);

internal sealed class FakeConversationService : IConversationService
{
    private readonly List<ServiceCall> calls = [];

    public IReadOnlyList<ServiceCall> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public PostResult PostResult { get; set; } = new(PostOutcome.Posted, Guid.Parse("11111111-1111-1111-1111-111111111111"), 1);

    public MessagePage Page { get; set; } = new(ListOutcome.Listed, []);

    public MarkReadResult MarkRead { get; set; } = new(MarkReadOutcome.Marked, 5);

    public Task<PostResult> PostAsync(ConversationActor actor, string tenantId, Guid conversationId, PostMessage message, CancellationToken cancellationToken = default)
    {
        Record("Post", actor, tenantId, conversationId, message);
        return Task.FromResult(PostResult);
    }

    public Task<MessagePage> ListAsync(ConversationActor actor, string tenantId, Guid conversationId, long afterSeq, int limit, CancellationToken cancellationToken = default)
    {
        Record("List", actor, tenantId, conversationId, (afterSeq, limit));
        return Task.FromResult(Page);
    }

    public Task<MarkReadResult> MarkReadAsync(ConversationActor actor, string tenantId, Guid conversationId, long seq, CancellationToken cancellationToken = default)
    {
        Record("MarkRead", actor, tenantId, conversationId, seq);
        return Task.FromResult(MarkRead);
    }

    public Task<OpenResult> OpenAsync(ConversationActor actor, string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<JoinResult> JoinAsync(ConversationActor actor, string tenantId, Guid conversationId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CloseResult> CloseAsync(ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private void Record(string method, ConversationActor actor, string tenantId, Guid conversationId, object? argument)
    {
        lock (calls)
        {
            calls.Add(new ServiceCall(method, actor, tenantId, conversationId, argument));
        }
    }
}

internal sealed class FakeGuestAccess : IGuestAccess
{
    private readonly Dictionary<(string Tenant, string Token), GuestSessionResult> sessions = [];
    private readonly List<(string Tenant, string Token)> calls = [];

    public IReadOnlyList<(string Tenant, string Token)> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public void Allow(string tenant, string token, Guid conversationId, GuestSessionKey key, DateTimeOffset expiresAt) =>
        sessions[(tenant, token)] = new GuestSessionResult(GuestExchangeOutcome.Established, conversationId, key, expiresAt);

    public Task<GuestSessionResult> ExchangeAsync(string tenantId, string token, CancellationToken cancellationToken = default)
    {
        lock (calls)
        {
            calls.Add((tenantId, token));
        }

        return Task.FromResult(sessions.TryGetValue((tenantId, token), out var result) ? result : new GuestSessionResult(GuestExchangeOutcome.Refused, null, null, null));
    }
}

internal sealed class FakeConversationAttachments : IConversationAttachments
{
    public List<(PostMessage Message, List<(string FileName, string ContentType, long Length)> Files)> Posts { get; } = [];

    public int Opens { get; private set; }

    public PostResult PostResult { get; set; } = new(PostOutcome.Posted, Guid.Parse("22222222-2222-2222-2222-222222222222"), 2);

    public OpenAttachmentResult OpenResult { get; set; } = new(
        OpenAttachmentOutcome.Opened,
        new Uri("https://files.example/signed?x=1"),
        new AttachmentInfo(Guid.Parse("33333333-3333-3333-3333-333333333333"), "a.png", "image/png", 3));

    public async Task<PostResult> PostAsync(ConversationActor actor, string tenantId, Guid conversationId, PostMessage message, IReadOnlyList<AttachmentUpload> files, CancellationToken cancellationToken = default)
    {
        var seen = new List<(string, string, long)>();
        foreach (var file in files)
        {
            using var copy = new MemoryStream();
            await file.Content.CopyToAsync(copy, cancellationToken);
            seen.Add((file.FileName, file.ContentType, copy.Length));
        }

        lock (Posts)
        {
            Posts.Add((message, seen));
        }

        return PostResult;
    }

    public Task<OpenAttachmentResult> OpenAsync(ConversationActor actor, string tenantId, Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        Opens++;
        return Task.FromResult(OpenResult);
    }
}
