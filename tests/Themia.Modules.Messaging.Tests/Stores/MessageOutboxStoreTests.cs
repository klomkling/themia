using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Messaging.Messages;
using Themia.Modules.Messaging.Entities;
using Themia.Modules.Messaging.Stores;

using Xunit;

namespace Themia.Modules.Messaging.Tests.Stores;

public class MessageOutboxStoreTests
{
    private sealed class RecordingRepository : IRepository<MessageOutboxEntry, Guid>
    {
        public List<MessageOutboxEntry> Added { get; } = [];

        public Task AddAsync(MessageOutboxEntry entity, CancellationToken ct = default)
        {
            Added.Add(entity);
            return Task.CompletedTask;
        }

        // The store only ever calls AddAsync; the rest of IRepository is not exercised.
        public Task<MessageOutboxEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<MessageOutboxEntry>> ListAsync(ISpecification<MessageOutboxEntry> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<MessageOutboxEntry?> FirstOrDefaultAsync(ISpecification<MessageOutboxEntry> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<long> CountAsync(ISpecification<MessageOutboxEntry> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<bool> AnyAsync(ISpecification<MessageOutboxEntry> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PagedResult<MessageOutboxEntry>> PageAsync(ISpecification<MessageOutboxEntry> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void Update(MessageOutboxEntry entity) => throw new NotSupportedException();
        public void Remove(MessageOutboxEntry entity) => throw new NotSupportedException();
        public Task<int> UpdateWhereAsync(ISpecification<MessageOutboxEntry> specification, Action<IBulkUpdateSetters<MessageOutboxEntry>> set, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static MessageEnvelope Valid() => new()
    {
        MessageId = Guid.CreateVersion7(),
        Type = "listing.snapshot.v1",
        Payload = """{"id":42}""",
        Destination = "propertiezy",
        Origin = "ezy-assets",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task EnqueueAsync_ShouldStageOneRow_WithPendingStatusAndZeroAttempts()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal(OutboxStatus.Pending, entry.Status);
        Assert.Equal(0, entry.Attempts);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldCarryEnvelopeFieldsVerbatim()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);
        var envelope = Valid();

        await store.EnqueueAsync(envelope, CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal(envelope.MessageId, entry.MessageId);
        Assert.Equal(envelope.Type, entry.Type);
        Assert.Equal(envelope.Payload, entry.Payload);
        Assert.Equal(envelope.Destination, entry.Destination);
        Assert.Equal(envelope.Origin, entry.Origin);
    }

    // A row must be due immediately unless the caller scheduled it, or it would never be claimed.
    [Fact]
    public async Task EnqueueAsync_ShouldSetNextAttemptAt_ToNow_WhenNotScheduled()
    {
        var now = new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, new FixedTimeProvider(now));

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal(now, entry.NextAttemptAt);
        Assert.Null(entry.ScheduledFor);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldSerializeHeaders_AsJson()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);
        var envelope = Valid();
        envelope.Headers = new Dictionary<string, string> { ["x-trace"] = "abc" };

        await store.EnqueueAsync(envelope, CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal("""{"x-trace":"abc"}""", entry.Headers);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldLeaveHeadersNull_WhenNoneSupplied()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        Assert.Null(Assert.Single(repository.Added).Headers);
    }

    // Validation runs at enqueue so a malformed message fails at the call site, not hours later in the drainer.
    [Fact]
    public async Task EnqueueAsync_ShouldThrow_WhenEnvelopeIsInvalid()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);
        var envelope = Valid();
        envelope.Origin = string.Empty;

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.EnqueueAsync(envelope, CancellationToken.None));
        Assert.Empty(repository.Added);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldThrow_WhenEnvelopeIsNull()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            () => new MessageOutboxStore(new RecordingRepository(), TimeProvider.System)
                .EnqueueAsync(null!, CancellationToken.None));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
