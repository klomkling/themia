using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfTemplateTests
{
    [Fact]
    public void Global_template_has_null_tenant()
    {
        var t = new PdfTemplate { Key = "invoice", Body = "<p>{{Total}}</p>", TenantId = null };
        Assert.Null(t.TenantId);
        Assert.False(t.IsDeleted);
    }

    [Fact]
    public void Not_found_exception_is_http_agnostic()
    {
        var ex = new TemplateNotFoundException("invoice");
        Assert.Contains("invoice", ex.Message);
        Assert.Null(ex.GetType().GetProperty("StatusCode")); // no HTTP coupling
    }
}
