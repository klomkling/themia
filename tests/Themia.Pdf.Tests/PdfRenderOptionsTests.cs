using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class PdfRenderOptionsTests
{
    [Fact]
    public void Defaults_MatchPortedEzyValues()
    {
        var o = new PdfRenderOptions();

        Assert.Equal(PdfPaperFormat.A4, o.PaperFormat);
        Assert.True(o.PrintBackground);
        Assert.Equal("20mm", o.MarginTop);
        Assert.Equal("20mm", o.MarginBottom);
        Assert.Equal("15mm", o.MarginLeft);
        Assert.Equal("15mm", o.MarginRight);
    }
}
