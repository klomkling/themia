using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Events;
using Themia.Framework.Core.Extensions;
using Xunit;

namespace Themia.Framework.Core.Tests.Extensions;

public class EntityExtensionsTests
{
    [Fact]
    public async Task DispatchAndClearEventsAsync_SingleEntity_DispatchesThenClears()
    {
        var dispatcher = new RecordingDispatcher();
        var entity = new TestEntity(Guid.NewGuid());
        entity.Raise(new TestDomainEvent());

        await entity.DispatchAndClearEventsAsync(dispatcher);

        Assert.Single(dispatcher.DispatchedEvents);
        Assert.Empty(entity.DomainEvents);
    }

    [Fact]
    public async Task DispatchAndClearEventsAsync_MultipleEntities_DispatchesAllThenClearsAll()
    {
        var dispatcher = new RecordingDispatcher();
        var first = new TestEntity(Guid.NewGuid());
        var second = new TestEntity(Guid.NewGuid());
        first.Raise(new TestDomainEvent());
        second.Raise(new TestDomainEvent());

        await dispatcher.DispatchAndClearEventsAsync(CancellationToken.None, first, second);

        Assert.Equal(2, dispatcher.DispatchedEvents.Count);
        Assert.Empty(first.DomainEvents);
        Assert.Empty(second.DomainEvents);
    }

    [Fact]
    public async Task DispatchAndClearEventsAsync_NoEvents_DoesNotInvokeDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var entity = new TestEntity(Guid.NewGuid());

        await dispatcher.DispatchAndClearEventsAsync(CancellationToken.None, entity);

        Assert.Equal(0, dispatcher.DispatchCallCount);
    }

    [Fact]
    public async Task DispatchAndClearEventsAsync_DoesNotClearWhenDispatchThrows()
    {
        // Events must survive a failed dispatch so an outer retry / UoW rollback can re-dispatch them.
        var dispatcher = new RecordingDispatcher { ThrowOnDispatch = true };
        var entity = new TestEntity(Guid.NewGuid());
        entity.Raise(new TestDomainEvent());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => entity.DispatchAndClearEventsAsync(dispatcher));

        Assert.Single(entity.DomainEvents);
    }

    [Fact]
    public void WithPendingEvents_ReturnsOnlyEntitiesWithEvents()
    {
        var withEvents = new TestEntity(Guid.NewGuid());
        withEvents.Raise(new TestDomainEvent());
        var withoutEvents = new TestEntity(Guid.NewGuid());

        var pending = new[] { withEvents, withoutEvents }.WithPendingEvents().ToList();

        Assert.Single(pending);
        Assert.Same(withEvents, pending[0]);
    }

    [Fact]
    public void PartitionByState_SeparatesTransientFromPersisted()
    {
        var transient = new TestEntity(Guid.Empty);
        var persisted = new TestEntity(Guid.NewGuid());

        var (transientEntities, persistedEntities) =
            new[] { transient, persisted }.PartitionByState();

        Assert.Same(transient, Assert.Single(transientEntities));
        Assert.Same(persisted, Assert.Single(persistedEntities));
    }

    [Fact]
    public void AddSafely_AssignsId_ToTransientEntity()
    {
        // The whole point of AddSafely: a transient Guid entity must get a stable Id before
        // entering the collection, otherwise its hash code shifts after later persistence.
        var collection = new List<Entity<Guid>>();
        var transient = new TestEntity(Guid.Empty);

        collection.AddSafely(transient);

        Assert.Single(collection);
        Assert.False(transient.IsTransient);
        Assert.NotEqual(Guid.Empty, transient.Id);
    }

    [Fact]
    public void AddSafely_DoesNotReassignId_ForPersistedEntity()
    {
        var existingId = Guid.NewGuid();
        var collection = new List<Entity<Guid>>();
        var persisted = new TestEntity(existingId);

        collection.AddSafely(persisted);

        Assert.Equal(existingId, persisted.Id);
    }

    private sealed record TestDomainEvent : DomainEventBase;

    private sealed class TestEntity : Entity<Guid>
    {
        public TestEntity(Guid id) => Id = id;

        public void Raise(IDomainEvent evt) => AddDomainEvent(evt);
    }

    private sealed class RecordingDispatcher : IDomainEventDispatcher
    {
        public List<IDomainEvent> DispatchedEvents { get; } = [];
        public int DispatchCallCount { get; private set; }
        public bool ThrowOnDispatch { get; init; }

        public Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            DispatchCallCount++;
            if (ThrowOnDispatch)
            {
                throw new InvalidOperationException("dispatch failed");
            }

            DispatchedEvents.Add(domainEvent);
            return Task.CompletedTask;
        }

        public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
        {
            DispatchCallCount++;
            if (ThrowOnDispatch)
            {
                throw new InvalidOperationException("dispatch failed");
            }

            DispatchedEvents.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }
}
