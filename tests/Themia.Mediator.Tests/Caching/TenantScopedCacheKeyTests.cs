using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Infrastructure;

namespace Themia.Mediator.Tests.Caching;

/// <summary>
/// Verifies that <see cref="DefaultCacheKeyFactory"/> produces tenant-scoped keys so that
/// cached query responses are never shared across tenants.
/// </summary>
public sealed class TenantScopedCacheKeyTests
{
    private readonly DefaultCacheKeyFactory _factory = new();

    [Fact]
    public void CreateKey_produces_different_keys_for_different_tenants()
    {
        var request = new TenantTestQuery("x");

        string keyA;
        string keyB;

        TenantContextAccessor.CurrentTenantId = new TenantId("tenant-a");
        try
        {
            keyA = _factory.CreateKey(request);
        }
        finally
        {
            TenantContextAccessor.CurrentTenantId = null;
        }

        TenantContextAccessor.CurrentTenantId = new TenantId("tenant-b");
        try
        {
            keyB = _factory.CreateKey(request);
        }
        finally
        {
            TenantContextAccessor.CurrentTenantId = null;
        }

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void CreateKey_no_tenant_differs_from_tenant_key()
    {
        var request = new TenantTestQuery("x");

        string keyNoTenant;
        string keyTenant;

        TenantContextAccessor.CurrentTenantId = null;
        keyNoTenant = _factory.CreateKey(request);

        TenantContextAccessor.CurrentTenantId = new TenantId("tenant-a");
        try
        {
            keyTenant = _factory.CreateKey(request);
        }
        finally
        {
            TenantContextAccessor.CurrentTenantId = null;
        }

        Assert.NotEqual(keyNoTenant, keyTenant);
    }

    [Fact]
    public void CreateKey_custom_provider_is_also_tenant_prefixed()
    {
        var request = new TenantCustomKeyQuery("42");

        string keyA;
        string keyB;

        TenantContextAccessor.CurrentTenantId = new TenantId("tenant-a");
        try
        {
            keyA = _factory.CreateKey(request);
        }
        finally
        {
            TenantContextAccessor.CurrentTenantId = null;
        }

        TenantContextAccessor.CurrentTenantId = new TenantId("tenant-b");
        try
        {
            keyB = _factory.CreateKey(request);
        }
        finally
        {
            TenantContextAccessor.CurrentTenantId = null;
        }

        Assert.NotEqual(keyA, keyB);
        Assert.StartsWith("t:tenant-a:", keyA);
        Assert.StartsWith("t:tenant-b:", keyB);
    }

    // Test types
    private sealed record TenantTestQuery(string Value) : IQuery<string>;

    private sealed record TenantCustomKeyQuery(string Id) : IQuery<string>, ICacheKeyProvider
    {
        public string GetCacheKey() => $"order:{Id}";
        public string? GetCacheKeyPrefix() => "order:";
    }
}
