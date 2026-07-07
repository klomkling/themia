using Microsoft.EntityFrameworkCore;
using Themia.Modules.Pdf;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfDbContextModelTests
{
    private static PdfDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<PdfDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PdfDbContext(options);
    }

    [Fact]
    public void Maps_pdf_template_to_expected_table_and_columns()
    {
        using var ctx = NewContext();
        var entity = ctx.Model.FindEntityType(typeof(PdfTemplate))!;
        Assert.Equal("pdf_templates", entity.GetTableName());
        Assert.Null(entity.GetSchema());
        Assert.Equal("tenant_id", entity.FindProperty("TenantId")!.GetColumnName());
        Assert.Equal("key", entity.FindProperty("Key")!.GetColumnName());
        Assert.Equal("body", entity.FindProperty("Body")!.GetColumnName());
    }
}
