using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Messaging.Messages;
using Themia.Modules.Messaging;
using Themia.Modules.Messaging.Entities;
using Themia.Modules.Messaging.Stores;

using Xunit;

namespace Themia.Modules.Messaging.Tests.Stores;

public class MessageOutboxStoreTests
{
    private static MessagingModuleOptions Options(string origin = "configured-origin") =>
        new() { ConnectionStringName = "Default", Origin = origin };

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
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options());

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal(OutboxStatus.Pending, entry.Status);
        Assert.Equal(0, entry.Attempts);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldCarryEnvelopeFieldsVerbatim()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options());
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
        var store = new MessageOutboxStore(repository, new FixedTimeProvider(now), Options());

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal(now, entry.NextAttemptAt);
        Assert.Null(entry.ScheduledFor);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldSerializeHeaders_AsJson()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options());
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
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options());

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        Assert.Null(Assert.Single(repository.Added).Headers);
    }

    // Validation runs at enqueue so a malformed message fails at the call site, not hours later in the drainer.
    // Type (not Origin — see F2 tests above) is still required, so it exercises the same guard.
    [Fact]
    public async Task EnqueueAsync_ShouldThrow_WhenEnvelopeIsInvalid()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options());
        var envelope = Valid();
        envelope.Type = string.Empty;

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.EnqueueAsync(envelope, CancellationToken.None));
        Assert.Empty(repository.Added);
    }

    // F2: Origin is no longer required by Validate() — an envelope may omit it entirely and fall back to
    // the module's configured Origin at store level.
    [Fact]
    public async Task EnqueueAsync_ShouldNotThrow_WhenEnvelopeOriginIsBlank()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options());
        var envelope = Valid();
        envelope.Origin = string.Empty;

        await store.EnqueueAsync(envelope, CancellationToken.None);

        Assert.Single(repository.Added);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldThrow_WhenEnvelopeIsNull()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            () => new MessageOutboxStore(new RecordingRepository(), TimeProvider.System, Options())
                .EnqueueAsync(null!, CancellationToken.None));

    // F1: an explicit envelope TenantId must be carried onto the entry so it lands under that tenant
    // instead of the ambient one the repository would otherwise stamp.
    [Fact]
    public async Task EnqueueAsync_ShouldCarryExplicitTenantId_OntoTheEntry()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options());
        var envelope = Valid();
        envelope.TenantId = "tenant-x";

        await store.EnqueueAsync(envelope, CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal("tenant-x", entry.TenantId?.Value);
    }

    // F1: a null/blank envelope TenantId must leave the entry's TenantId null so the repository's
    // ambient-tenant stamping (only applied when the entity's TenantId is still null) still applies.
    [Fact]
    public async Task EnqueueAsync_ShouldLeaveTenantIdNull_WhenEnvelopeTenantIdNotSet()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options());

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        Assert.Null(Assert.Single(repository.Added).TenantId);
    }

    // F2: when the envelope leaves Origin unset, the module's configured Origin must be used instead —
    // otherwise the receiver's dedup key (origin, message_id) is built from an empty string.
    [Fact]
    public async Task EnqueueAsync_ShouldUseConfiguredOrigin_WhenEnvelopeOriginNotSet()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options("configured-origin"));
        var envelope = Valid();
        envelope.Origin = string.Empty;

        await store.EnqueueAsync(envelope, CancellationToken.None);

        Assert.Equal("configured-origin", Assert.Single(repository.Added).Origin);
    }

    // F2: an explicit envelope Origin must still win over the module's configured fallback.
    [Fact]
    public async Task EnqueueAsync_ShouldPreferEnvelopeOrigin_OverConfiguredOrigin()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, Options("configured-origin"));
        var envelope = Valid();
        envelope.Origin = "envelope-origin";

        await store.EnqueueAsync(envelope, CancellationToken.None);

        Assert.Equal("envelope-origin", Assert.Single(repository.Added).Origin);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
