using Themia.Framework.Core.Abstractions.Entities;
using Xunit;

namespace Themia.Framework.Core.Tests.Abstractions;

public class SoftDeletableEntityTests
{
    [Fact]
    public void MarkDeleted_SetsDeletionMetadata()
    {
        var when = DateTimeOffset.UtcNow;
        var entity = new TestEntity();

        entity.Delete(when, "alice");

        Assert.True(entity.IsDeleted);
        Assert.Equal(when, entity.DeletedAt);
        Assert.Equal("alice", entity.DeletedBy);
    }

    [Fact]
    public void Restore_ClearsDeletionMetadata_AndSetsRestoreMetadata()
    {
        var entity = new TestEntity();
        entity.Delete(DateTimeOffset.UtcNow, "alice");

        var when = DateTimeOffset.UtcNow;
        entity.Undelete(when, "bob");

        Assert.False(entity.IsDeleted);
        Assert.Equal(when, entity.RestoredAt);
        Assert.Equal("bob", entity.RestoredBy);
        // Restore must not leave stale deletion metadata behind.
        Assert.Null(entity.DeletedAt);
        Assert.Null(entity.DeletedBy);
    }

    [Fact]
    public void Delete_AfterRestore_ClearsStaleRestoreMetadata()
    {
        var entity = new TestEntity();
        entity.Delete(DateTimeOffset.UtcNow, "alice");
        entity.Undelete(DateTimeOffset.UtcNow, "bob");

        entity.Delete(DateTimeOffset.UtcNow, "carol");

        Assert.True(entity.IsDeleted);
        // The delete→restore→delete cycle must not carry the prior restore fields.
        Assert.Null(entity.RestoredAt);
        Assert.Null(entity.RestoredBy);
    }

    private sealed class TestEntity : SoftDeletableEntity<Guid>
    {
        public void Delete(DateTimeOffset when, string? by) => MarkDeleted(when, by);

        public void Undelete(DateTimeOffset when, string? by) => Restore(when, by);
    }
}
