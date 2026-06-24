using Themia.Export.Internal;
using Xunit;

namespace Themia.Export.Tests;

public sealed class AggregateComputerTests
{
    [Fact]
    public void Sum_folds_numeric_skips_null()
    {
        object? r = AggregateComputer.Compute(AggregateKind.Sum, "T", new object?[] { 10, null, 2.5m });
        Assert.Equal(12.5m, r);
    }

    [Fact]
    public void Count_counts_non_null()
    {
        Assert.Equal(2m, AggregateComputer.Compute(AggregateKind.Count, "T", new object?[] { 1, null, "x" }));
    }

    [Fact]
    public void Average_blank_when_empty()
    {
        Assert.Null(AggregateComputer.Compute(AggregateKind.Average, "T", new object?[] { null, null }));
    }

    [Fact]
    public void Min_and_Max_over_numeric()
    {
        Assert.Equal(2m, AggregateComputer.Compute(AggregateKind.Min, "T", new object?[] { 5, 2, 9 }));
        Assert.Equal(9m, AggregateComputer.Compute(AggregateKind.Max, "T", new object?[] { 5, 2, 9 }));
    }

    [Fact]
    public void Label_returns_title_None_returns_null()
    {
        Assert.Equal("Total", AggregateComputer.Compute(AggregateKind.Label, "Total", new object?[] { 1 }));
        Assert.Null(AggregateComputer.Compute(AggregateKind.None, "T", new object?[] { 1 }));
    }

    [Fact]
    public void Non_numeric_in_numeric_aggregate_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AggregateComputer.Compute(AggregateKind.Sum, "Amount", new object?[] { 1, "oops" }));
        Assert.Contains("Amount", ex.Message);
    }
}
