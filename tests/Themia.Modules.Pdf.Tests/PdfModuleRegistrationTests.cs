using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Pdf.Rendering;
using Themia.Modules.Pdf.Store;
using Themia.Pdf;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfModuleRegistrationTests
{
    [Fact]
    public void EfCore_entry_point_registers_store_and_renderer_as_scoped()
    {
        var services = new ServiceCollection();
        services.AddThemiaPdfModuleEfCore();

        Assert.Contains(services, d => d.ServiceType == typeof(IPdfTemplateStore) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(IPdfDocumentRenderer) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void Dapper_entry_point_registers_dapper_store()
    {
        var services = new ServiceCollection();
        services.AddThemiaPdfModuleDapper();
        Assert.Contains(services, d => d.ServiceType == typeof(IPdfTemplateStore)
            && d.ImplementationType == typeof(DapperPdfTemplateStore) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void EfCore_entry_point_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddThemiaPdfModuleEfCore();
        services.AddThemiaPdfModuleEfCore();
        Assert.Single(services, d => d.ServiceType == typeof(IPdfDocumentRenderer));
    }

    [Fact]
    public async Task Scoped_lifetime_prevents_tenant_capture_across_scopes()
    {
        var services = new ServiceCollection();
        services.AddThemiaPdfModuleEfCore();

        // Fakes so the test needs no database and no headless Chromium: a scoped ITenantContext that
        // snapshots the ambient tenant at resolution time, a store that echoes that tenant into the
        // template body, and a pass-through render pipeline.
        services.AddScoped<ITenantContext>(_ => new TenantContext(TenantContextAccessor.CurrentTenantId));
        services.AddScoped<IPdfTemplateStore, TenantEchoStore>();
        services.AddSingleton<IHtmlTemplateRenderer, PassthroughHtmlTemplateRenderer>();
        services.AddSingleton<IPdfRenderer, PassthroughPdfRenderer>();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var previousTenant = TenantContextAccessor.CurrentTenantId;
        try
        {
            TenantContextAccessor.CurrentTenantId = new TenantId("tenant-a");
            using var scopeA = provider.CreateScope();
            var rendererA = scopeA.ServiceProvider.GetRequiredService<IPdfDocumentRenderer>();
            var bytesA = await rendererA.RenderAsync("invoice", new object());

            TenantContextAccessor.CurrentTenantId = new TenantId("tenant-b");
            using var scopeB = provider.CreateScope();
            var rendererB = scopeB.ServiceProvider.GetRequiredService<IPdfDocumentRenderer>();
            var bytesB = await rendererB.RenderAsync("invoice", new object());

            // Each scope must observe its own ambient tenant, not the first scope's captured value —
            // proving IPdfDocumentRenderer/IPdfTemplateStore are resolved fresh per scope rather than
            // captured once (e.g. as singletons) and reused across tenants.
            Assert.Equal("tenant-a", Encoding.UTF8.GetString(bytesA));
            Assert.Equal("tenant-b", Encoding.UTF8.GetString(bytesB));
        }
        finally
        {
            TenantContextAccessor.CurrentTenantId = previousTenant;
        }
    }

    private sealed class TenantEchoStore(ITenantContext tenantContext) : IPdfTemplateStore
    {
        public Task<PdfTemplate> ResolveAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PdfTemplate
            {
                Key = key,
                Body = tenantContext.CurrentTenantId?.Value ?? "none",
            });

        public Task<PdfTemplate> CreateAsync(PdfTemplate template, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PdfTemplate> UpdateAsync(PdfTemplate template, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PdfTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class PassthroughHtmlTemplateRenderer : IHtmlTemplateRenderer
    {
        public string Render(string template, object model) => template;
    }

    private sealed class PassthroughPdfRenderer : IPdfRenderer
    {
        public Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(Encoding.UTF8.GetBytes(html));
    }
}
