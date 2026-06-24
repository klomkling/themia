using Themia.Export;
using Xunit;

namespace Themia.Export.Tests;

public sealed class ContractTypeTests
{
    [Fact]
    public void ExportColumn_defaults_are_none_and_auto()
    {
        var col = new ExportColumn<string> { Title = "Name", Value = s => s };

        Assert.Equal(AggregateKind.None, col.Aggregate);
        Assert.Equal(ColumnAlignment.Auto, col.Alignment);
        Assert.Null(col.NumberFormat);
        Assert.Null(col.Width);
        Assert.Equal("hi", col.Value("hi"));
    }

    [Fact]
    public void ExportResult_carries_bytes_type_and_name()
    {
        var r = new ExportResult([1, 2, 3], "text/csv", "x.csv");

        Assert.Equal([1, 2, 3], r.Content);
        Assert.Equal("text/csv", r.ContentType);
        Assert.Equal("x.csv", r.FileName);
    }
}
