using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Storage;
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
}
