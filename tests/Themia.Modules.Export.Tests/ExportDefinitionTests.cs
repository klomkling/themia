using System.ComponentModel.DataAnnotations;
using Themia.Export;
using Themia.Export.Csv;
using Themia.Export.Excel;
using Themia.Modules.Export;
using Themia.Modules.Export.Definitions;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportDefinitionTests
{
    private sealed record Sale(string Product, decimal Amount);
    private sealed class SaleParams { [Range(0, int.MaxValue)] public int MinAmount { get; set; } }

    private sealed class SaleDef(ICsvExporter csv, IExcelExporter excel) : ExportDefinition<Sale, SaleParams>(csv, excel)
    {
        public override string Key => "sales";
        protected override IReadOnlyList<ExportColumn<Sale>> Columns(SaleParams p, ExportContext c) =>
            [new() { Title = "Product", Selector = s => s.Product }, new() { Title = "Amount", Selector = s => s.Amount, Aggregate = AggregateKind.Sum }];
        protected override Task<IReadOnlyList<Sale>> RowsAsync(SaleParams p, ExportContext c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Sale>>([new("Apple", 10m), new("Pear", 5m)]);
    }

    private static SaleDef NewDef() => new(new CsvExporter(), new ExcelExporter());

    [Fact]
    public async Task Csv_format_produces_csv_bytes_with_summary()
    {
        var result = await NewDef().ExportAsync(new ExportContext { TenantId = null, Format = ExportFormat.Csv }, default);
        Assert.Equal("text/csv", result.ContentType);
        Assert.Contains("15", System.Text.Encoding.UTF8.GetString(result.Content));
    }

    [Fact]
    public async Task Invalid_params_throw_InvalidOperationException()
    {
        var ctx = new ExportContext { TenantId = null, Format = ExportFormat.Csv, ParametersJson = "{\"MinAmount\":-1}" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewDef().ExportAsync(ctx, default));
    }
}
