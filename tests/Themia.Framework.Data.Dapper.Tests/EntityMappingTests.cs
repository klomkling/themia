using Themia.Framework.Data.Dapper.Mapping;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

public sealed class EntityMappingTests
{
    private sealed class AssetCategory { public int Id { get; set; } public string? DisplayName { get; set; } }

    [Fact]
    public void Convention_PluralizesTable_AndSnakeCasesColumns()
    {
        var map = EntityMapping.ForConvention<AssetCategory>();
        Assert.Equal("asset_categories", map.Table);
        Assert.Equal("id", map.KeyColumn);
        Assert.Equal("display_name", map.Column(nameof(AssetCategory.DisplayName)));
    }

    [Fact]
    public void ToSnakeCase_HandlesAcronymsAndDigits()
    {
        Assert.Equal("created_at", EntityMapping.ToSnakeCase("CreatedAt"));
        Assert.Equal("tenant_id", EntityMapping.ToSnakeCase("TenantId"));
        Assert.Equal("html_url", EntityMapping.ToSnakeCase("HtmlUrl"));
    }
}
