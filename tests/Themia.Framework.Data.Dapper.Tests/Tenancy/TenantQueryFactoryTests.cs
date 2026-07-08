using SqlKata.Compilers;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Tenancy;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests.Tenancy;

public sealed class TenantQueryFactoryTests
{
    private sealed class Widget : ITenantEntity
    {
        public int Id { get; set; }
        public TenantId? TenantId { get; set; }
    }

    private TenantQueryFactory Factory(bool appOptionIncludesGlobals)
        => new(new EntityMappingRegistry(),
            new FakeTenantContext(new TenantId("acme")),
            new DataFilterScope(),
            new DapperDataOptions { IncludeGlobalRecordsForTenants = appOptionIncludesGlobals });

    [Fact]
    public void For_with_includeGlobalRecords_true_emits_is_null_clause_even_when_app_option_is_false()
    {
        var sql = new SqlServerCompiler().Compile(Factory(false).For<Widget>(includeGlobalRecords: true)).ToString();
        Assert.Contains("Is Null", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void For_with_includeGlobalRecords_false_omits_is_null_clause_even_when_app_option_is_true()
    {
        var sql = new SqlServerCompiler().Compile(Factory(true).For<Widget>(includeGlobalRecords: false)).ToString();
        Assert.DoesNotContain("Is Null", sql, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeTenantContext(TenantId? id) : ITenantContext
    {
        public TenantId? CurrentTenantId { get; } = id;

        public string? Source => null;
    }
}
