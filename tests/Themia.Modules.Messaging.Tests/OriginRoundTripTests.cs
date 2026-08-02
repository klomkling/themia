using Microsoft.Extensions.DependencyInjection;

using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Messaging;
using Themia.Messaging.AspNetCore;
using Themia.Messaging.DependencyInjection;
using Themia.Messaging.Hmac;
using Themia.Messaging.Messages;
using Themia.Modules.Messaging.Entities;
using Themia.Modules.Messaging.Stores;

using Xunit;

namespace Themia.Modules.Messaging.Tests;

/// <summary>
/// Pins the invariant this whole change exists for: the origin the outbox STAMPS and the origin the loop
/// guard COMPARES come from one registration, so they cannot drift.
/// </summary>
/// <remarks>
/// Every other test proves one half. MessageOutboxStoreTests builds a MessagingIdentity by hand;
/// RoundTripTests hand-builds the outbox row with an origin it chose and registers the receiver's identity
/// separately. Both would keep passing if someone reintroduced a second origin source on the receiving
/// side — for instance a future VerificationOptions.Origin overriding identity.Origin in the filter —
/// which is exactly the drift that silently disables loop protection in production. These tests resolve
/// ONE MessagingIdentity from ONE container and drive both halves from it.
/// </remarks>
public class OriginRoundTripTests
{
    private const string Origin = "svc-a";

    [Fact]
    public async Task StampedOrigin_IsTheSameValueTheLoopGuardCompares()
    {
        // ONE registration, resolved once. Both halves below read this instance and nothing else.
        var provider = new ServiceCollection().AddThemiaMessagingIdentity(Origin).BuildServiceProvider();
        var identity = provider.GetRequiredService<MessagingIdentity>();

        // Half 1 — the stamp. An envelope that leaves Origin unset falls back to the service identity.
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, identity);
        await store.EnqueueAsync(Envelope());
        var stampedOrigin = Assert.Single(repository.Added).Origin;

        // Half 2 — the comparison. The stamped value comes back as the inbound header, as it would over
        // the wire, and is compared against the same identity.
        var headerNames = new HmacHeaderNames(HmacHeaderNames.DefaultPrefix);
        var inbound = new Dictionary<string, string?> { [headerNames.Origin] = stampedOrigin };

        Assert.True(LoopGuard.IsLoopback(inbound, headerNames, identity.Origin));
    }

    // The same wiring must NOT fire for a message that genuinely originated elsewhere — otherwise the
    // test above would pass just as well against a guard that always returns true.
    [Fact]
    public async Task StampedOrigin_DoesNotMatch_WhenTheEnvelopeCarriesAForeignOrigin()
    {
        var provider = new ServiceCollection().AddThemiaMessagingIdentity(Origin).BuildServiceProvider();
        var identity = provider.GetRequiredService<MessagingIdentity>();

        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, identity);
        var forwarded = Envelope();
        forwarded.Origin = "svc-b"; // a forwarded message keeps its ORIGINATOR, not the last hop
        await store.EnqueueAsync(forwarded);
        var stampedOrigin = Assert.Single(repository.Added).Origin;

        Assert.Equal("svc-b", stampedOrigin);

        var headerNames = new HmacHeaderNames(HmacHeaderNames.DefaultPrefix);
        var inbound = new Dictionary<string, string?> { [headerNames.Origin] = stampedOrigin };

        Assert.False(LoopGuard.IsLoopback(inbound, headerNames, identity.Origin));
    }

    // Padding on the configured origin must not break the match. HTTP strips optional whitespace around a
    // header value in transit, so an untrimmed identity would stamp "svc-a " and compare against an
    // inbound "svc-a" forever.
    [Fact]
    public async Task StampedOrigin_StillMatches_WhenTheConfiguredOriginWasPadded()
    {
        var provider = new ServiceCollection().AddThemiaMessagingIdentity("  " + Origin + "  ").BuildServiceProvider();
        var identity = provider.GetRequiredService<MessagingIdentity>();

        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System, identity);
        await store.EnqueueAsync(Envelope());
        var stampedOrigin = Assert.Single(repository.Added).Origin;

        Assert.Equal(Origin, stampedOrigin);

        var headerNames = new HmacHeaderNames(HmacHeaderNames.DefaultPrefix);
        var inbound = new Dictionary<string, string?> { [headerNames.Origin] = stampedOrigin };

        Assert.True(LoopGuard.IsLoopback(inbound, headerNames, identity.Origin));
    }

    private static MessageEnvelope Envelope() => new()
    {
        MessageId = Guid.CreateVersion7(),
        Type = "listing.snapshot.v1",
        Payload = "{}",
        Destination = "peer",
    };

    private sealed class RecordingRepository : IRepository<MessageOutboxEntry, Guid>
    {
        public List<MessageOutboxEntry> Added { get; } = [];

        public Task AddAsync(MessageOutboxEntry entity, CancellationToken ct = default)
        {
            Added.Add(entity);
            return Task.CompletedTask;
        }

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
}
