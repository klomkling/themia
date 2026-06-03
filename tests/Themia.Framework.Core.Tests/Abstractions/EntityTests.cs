using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Events;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Core.Tests.Abstractions;

public class EntityTests
{
    private sealed record TestDomainEvent : DomainEventBase;

    private sealed class TestEntity : Entity<Guid>, ITenantEntity
    {
        public TestEntity(Guid id, TenantId? tenantId = null)
        {
            Id = id;
            TenantId = tenantId;
        }

        public TenantId? TenantId { get; set; }

        public void Raise(IDomainEvent evt) => AddDomainEvent(evt);
    }

    private sealed class AuditableTestEntity : AuditableEntity<Guid>
    {
        public void MarkNew(DateTimeOffset createdAt, string? createdBy) =>
            MarkCreated(createdAt, createdBy);

        public void MarkUpdated(DateTimeOffset modifiedAt, string? modifiedBy) =>
            MarkModified(modifiedAt, modifiedBy);
    }

    [Fact]
    public void EntitiesWithSameId_AreEqual()
    {
        var id = Guid.NewGuid();
        var left = new TestEntity(id);
        var right = new TestEntity(id);

        Assert.True(left == right);
    }

    [Fact]
    public void DomainEvents_CanBeAddedAndCleared()
    {
        var entity = new TestEntity(Guid.NewGuid());
        var evt = new TestDomainEvent();

        entity.Raise(evt);

        Assert.Contains(evt, entity.DomainEvents);
        Assert.Single(entity.DomainEvents);

        entity.ClearDomainEvents();
        Assert.Empty(entity.DomainEvents);
    }

    [Fact]
    public void AuditableEntity_SetsCreatedAndModifiedMetadata()
    {
        var entity = new AuditableTestEntity();
        var createdAt = DateTimeOffset.UtcNow;
        var modifiedAt = createdAt.AddMinutes(1);

        entity.MarkNew(createdAt, "creator");
        entity.MarkUpdated(modifiedAt, "editor");

        Assert.Equal(createdAt, entity.CreatedAt);
        Assert.Equal("creator", entity.CreatedBy);
        Assert.Equal(modifiedAt, entity.LastModifiedAt);
        Assert.Equal("editor", entity.LastModifiedBy);
    }

    [Fact]
    public void TransientEntities_AreNotEqual()
    {
        var left = new TestEntity(default);
        var right = new TestEntity(default);

        Assert.True(left.IsTransient);
        Assert.True(right.IsTransient);
        Assert.False(left == right);
    }

    [Fact]
    public void TransientEntity_IsNotEqualToPersistedEntity()
    {
        var transient = new TestEntity(default);
        var persisted = new TestEntity(Guid.NewGuid());

        Assert.True(transient.IsTransient);
        Assert.False(persisted.IsTransient);
        Assert.False(transient == persisted);
    }

    [Fact]
    public void AddDomainEvent_ThrowsArgumentNullException_WhenEventIsNull()
    {
        var entity = new TestEntity(Guid.NewGuid());

        Assert.Throws<ArgumentNullException>(() => entity.Raise(null!));
    }

    [Fact]
    public void DomainEvent_HasOccurredAtTimestamp()
    {
        var evt = new TestDomainEvent();

        Assert.True(evt.OccurredAt <= DateTimeOffset.UtcNow);
        Assert.True(evt.OccurredAt >= DateTimeOffset.UtcNow.AddSeconds(-1));
    }
}
