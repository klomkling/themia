using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Tenancy;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

public sealed class TenantPredicateTests
{
    private sealed class Doc : Themia.Framework.Core.Abstractions.Tenancy.ITenantEntity,
                               Themia.Framework.Core.Abstractions.Entities.ISoftDeletable
    {
        public int Id { get; set; }
        public TenantId? TenantId { get; set; }
        public bool IsDeleted { get; }
        public System.DateTimeOffset? DeletedAt { get; }
        public string? DeletedBy { get; }
        public System.DateTimeOffset? RestoredAt { get; }
        public string? RestoredBy { get; }
    }

    private static readonly EntityMapping Map = EntityMapping.ForConvention<Doc>();

    private static string Sql(TenantId? tenant, bool bypass, bool includeGlobalRecords = true, bool bypassSoftDelete = false)
    {
        var q = new Query(Map.Table);
        TenantPredicate.Apply<Doc>(q, tenant, includeGlobalRecords, bypassTenantFilter: bypass, bypassSoftDeleteFilter: bypassSoftDelete, Map);
        return new PostgresCompiler().Compile(q).Sql.ToLowerInvariant();
    }

    [Fact]
    public void WithTenant_AddsTenantAndNotDeleted()
    {
        var sql = Sql(new TenantId("acme"), bypass: false);
        Assert.Contains("tenant_id", sql);
        Assert.Contains("is_deleted", sql);
    }

    [Fact]
    public void Bypass_OmitsTenant_ButKeepsSoftDelete()
    {
        var sql = Sql(new TenantId("acme"), bypass: true);
        Assert.DoesNotContain("tenant_id", sql);
        Assert.Contains("is_deleted", sql);
    }

    [Fact]
    public void NoTenant_NotBypassed_OnlyGlobalRecords()
    {
        var sql = Sql(null, bypass: false);
        Assert.Contains("tenant_id", sql);
        Assert.Contains("is null", sql);
    }

    [Fact]
    public void WithTenant_ExcludeGlobalRecords_UsesPlainEquality()
    {
        var sql = Sql(new TenantId("acme"), bypass: false, includeGlobalRecords: false);
        Assert.Contains("tenant_id", sql);
        Assert.DoesNotContain("is null", sql);
    }

    [Fact]
    public void BypassSoftDelete_OmitsSoftDeleteClause_KeepsTenantClause()
    {
        var sql = Sql(new TenantId("acme"), bypass: false, bypassSoftDelete: true);
        Assert.Contains("tenant_id", sql);
        Assert.DoesNotContain("is_deleted", sql);
    }
}
