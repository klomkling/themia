using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Pdf.Store;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfTemplateResolverTests
{
    private static PdfTemplate Tpl(string? tenant) =>
        new() { Key = "invoice", Body = "b", TenantId = tenant is null ? null : new TenantId(tenant) };

    [Fact]
    public void Prefers_tenant_row_over_global()
    {
        var chosen = PdfTemplateResolver.PreferTenant([Tpl("acme"), Tpl(null)]);
        Assert.Equal(new TenantId("acme"), chosen!.TenantId);
    }

    [Fact]
    public void Falls_back_to_global_when_no_tenant_row()
    {
        var chosen = PdfTemplateResolver.PreferTenant([Tpl(null)]);
        Assert.Null(chosen!.TenantId);
    }

    [Fact]
    public void Returns_null_when_no_candidates()
    {
        Assert.Null(PdfTemplateResolver.PreferTenant([]));
    }
}
