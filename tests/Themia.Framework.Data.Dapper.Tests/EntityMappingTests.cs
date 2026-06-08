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

    [Theory]
    [InlineData("CreatedAt", "created_at")]
    [InlineData("TenantId", "tenant_id")]
    [InlineData("HtmlUrl", "html_url")]
    [InlineData("APIUrl", "api_url")]
    [InlineData("IOStream", "io_stream")]
    [InlineData("Address1", "address1")]
    [InlineData("Line2Total", "line2_total")]
    public void ToSnakeCase_HandlesAcronymsAndDigits(string input, string expected)
    {
        Assert.Equal(expected, EntityMapping.ToSnakeCase(input));
    }
}
