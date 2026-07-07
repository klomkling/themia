using Themia.Data.Migrations;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfModuleTests
{
    [Fact]
    public void Descriptor_reports_module_identity()
    {
        var module = new PdfModule(MigrationEngine.Postgres);

        Assert.Equal("Themia.Pdf", module.Descriptor.Name);
        Assert.Equal(new Version(0, 7, 0, 0), module.Descriptor.Version);
    }
}
