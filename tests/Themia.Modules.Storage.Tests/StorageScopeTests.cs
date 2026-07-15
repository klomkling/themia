using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Storage;
using Themia.Storage;
using Xunit;

namespace Themia.Modules.Storage.Tests;

public sealed class StorageScopeTests
{
    [Fact]
    public void Tenant_key_is_prefixed_with_tenant_id()
    {
        Assert.Equal("acme/a/b.txt", StorageScope.PhysicalKey(new TenantId("acme"), "a/b.txt"));
    }

    [Fact]
    public void Platform_key_uses_the_platform_prefix()
    {
        Assert.Equal("_platform/a/b.txt", StorageScope.PhysicalKey(null, "a/b.txt"));
    }

    [Fact]
    public void Backslashes_are_normalized()
    {
        Assert.Equal("acme/a/b.txt", StorageScope.PhysicalKey(new TenantId("acme"), "a\\b.txt"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("/abs")]
    [InlineData("")]
    public void Invalid_keys_are_rejected(string key)
    {
        Assert.Throws<ArgumentException>(() => StorageScope.PhysicalKey(new TenantId("acme"), key));
    }

    [Fact]
    public void Tenant_id_equal_to_platform_prefix_is_rejected()
    {
        // A tenant id equal to "_platform" would collide with platform objects at the blob layer.
        Assert.Throws<ArgumentException>(() => StorageScope.PhysicalKey(new TenantId("_platform"), "k"));
    }

    [Fact]
    public void PhysicalKey_prefixes_public_objects_and_leaves_private_ones_byte_identical()
    {
        var tenant = new TenantId("t1");

        // BACK-COMPAT CONTRACT: a private key must be exactly what it was before this feature existed.
        // If this assertion ever changes, every already-stored blob would have to be moved.
        Assert.Equal("t1/a.jpg", StorageScope.PhysicalKey(tenant, "a.jpg", StorageVisibility.Private));
        Assert.Equal("t1/a.jpg", StorageScope.PhysicalKey(tenant, "a.jpg"));

        Assert.Equal("public/t1/a.jpg", StorageScope.PhysicalKey(tenant, "a.jpg", StorageVisibility.Public));
    }

    [Fact]
    public void PhysicalKey_prefixes_public_platform_objects()
    {
        Assert.Equal("public/_platform/a.jpg", StorageScope.PhysicalKey(null, "a.jpg", StorageVisibility.Public));
    }

    [Fact]
    public void PhysicalKey_rejects_the_reserved_public_tenant_id()
    {
        // A tenant named "public" would put its PRIVATE objects at public/{key} — inside the public namespace.
        var ex = Assert.Throws<ArgumentException>(() => StorageScope.PhysicalKey(new TenantId("public"), "a.jpg", StorageVisibility.Private));
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
