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
    public void ForConvention_AppliesTableAndColumnOverrides_KeepsConventionForTheRest()
    {
        var map = EntityMapping.ForConvention<AssetCategory>(
            table: "tbl_categories",
            columnOverrides: new Dictionary<string, string> { ["DisplayName"] = "label" });

        Assert.Equal("tbl_categories", map.Table);
        Assert.Equal("label", map.Column(nameof(AssetCategory.DisplayName)));
        Assert.Equal("id", map.KeyColumn);   // unspecified members keep the snake_case convention
    }

    [Fact]
    public void ForConvention_OverridingTheKeyColumn_UpdatesKeyColumn()
    {
        var map = EntityMapping.ForConvention<AssetCategory>(
            table: null,
            columnOverrides: new Dictionary<string, string> { ["Id"] = "category_id" });

        Assert.Equal("category_id", map.KeyColumn);
        Assert.Equal("category_id", map.Column(nameof(AssetCategory.Id)));
    }

    [Fact]
    public void ForConvention_OverrideForUnknownProperty_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntityMapping.ForConvention<AssetCategory>(
                table: null,
                columnOverrides: new Dictionary<string, string> { ["Nope"] = "x" }));

        Assert.Contains("Nope", ex.Message);
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
