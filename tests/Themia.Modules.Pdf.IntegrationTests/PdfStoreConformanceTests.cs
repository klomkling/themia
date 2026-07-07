using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Pdf;
using Themia.Modules.Pdf.Store;
using Xunit;

namespace Themia.Modules.Pdf.IntegrationTests;

/// <summary>Peer-agnostic conformance facts exercised against a real database by each data-layer peer
/// (EF Core, Dapper). A peer supplies its wiring via <see cref="ConfigurePeer"/> and a reset hook; the
/// same seven facts run for both, verifying the migration, both stores, tenant/global resolution, and
/// the tenant/global write-asymmetry on a real engine.</summary>
public abstract class PdfStoreConformanceTests
{
    protected abstract void ConfigurePeer(IServiceCollection services, IConfiguration configuration);
    protected abstract Task ResetAsync();
    protected abstract string ConnectionString { get; }

    protected sealed record Scope(ServiceProvider Provider, AsyncServiceScope Inner) : IAsyncDisposable
    {
        public IPdfTemplateStore Store => Inner.ServiceProvider.GetRequiredService<IPdfTemplateStore>();
        public async ValueTask DisposeAsync() { await Inner.DisposeAsync(); await Provider.DisposeAsync(); }
    }

    // tenant == null => system/global scope (can create global rows).
    protected Scope NewScope(TenantId? tenant)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        ConfigurePeer(services, configuration);
        var provider = services.BuildServiceProvider();
        return new Scope(provider, provider.CreateAsyncScope());
    }

    [Fact]
    public async Task Create_and_get_roundtrip()
    {
        await ResetAsync();
        Guid id;
        await using (var s = NewScope(new TenantId("acme")))
        {
            var created = await s.Store.CreateAsync(new PdfTemplate { Key = "k", Body = "v" });
            id = created.Id;
        }
        await using (var s = NewScope(new TenantId("acme")))
        {
            var fetched = await s.Store.GetAsync(id);
            Assert.NotNull(fetched);
            Assert.Equal("v", fetched!.Body);
        }
    }

    [Fact]
    public async Task Resolve_prefers_tenant_over_global()
    {
        await ResetAsync();
        await using (var system = NewScope(tenant: null))
        {
            await system.Store.CreateAsync(new PdfTemplate { Key = "invoice", Body = "GLOBAL", TenantId = null });
        }
        await using (var acme = NewScope(new TenantId("acme")))
        {
            await acme.Store.CreateAsync(new PdfTemplate { Key = "invoice", Body = "ACME" });
        }
        await using (var acme = NewScope(new TenantId("acme")))
        {
            var resolved = await acme.Store.ResolveAsync("invoice");
            Assert.Equal("ACME", resolved.Body);
        }
        await using (var other = NewScope(new TenantId("other")))
        {
            var resolved = await other.Store.ResolveAsync("invoice");
            Assert.Equal("GLOBAL", resolved.Body);
        }
    }

    [Fact]
    public async Task Resolve_falls_back_to_global()
    {
        await ResetAsync();
        await using (var system = NewScope(tenant: null))
        {
            await system.Store.CreateAsync(new PdfTemplate { Key = "note", Body = "GLOBAL-NOTE", TenantId = null });
        }
        await using (var acme = NewScope(new TenantId("acme")))
        {
            var resolved = await acme.Store.ResolveAsync("note");
            Assert.Equal("GLOBAL-NOTE", resolved.Body);
        }
    }

    [Fact]
    public async Task Resolve_throws_when_missing()
    {
        await ResetAsync();
        await using var acme = NewScope(new TenantId("acme"));
        await Assert.ThrowsAsync<TemplateNotFoundException>(() => acme.Store.ResolveAsync("nope"));
    }

    [Fact]
    public async Task Delete_soft_deletes()
    {
        await ResetAsync();
        await using var acme = NewScope(new TenantId("acme"));
        var created = await acme.Store.CreateAsync(new PdfTemplate { Key = "gone", Body = "v" });
        await acme.Store.DeleteAsync(created.Id);
        Assert.Null(await acme.Store.GetAsync(created.Id));
    }

    [Fact]
    public async Task Tenant_scope_cannot_create_global_row()
    {
        await ResetAsync();
        await using (var acme = NewScope(new TenantId("acme")))
        {
            await acme.Store.CreateAsync(new PdfTemplate { Key = "x", Body = "v", TenantId = null });
        }
        await using (var system = NewScope(tenant: null))
        {
            var all = await system.Store.ListAsync();
            Assert.DoesNotContain(all, t => t.Key == "x" && t.TenantId == null);
        }
    }

    [Fact]
    public async Task Duplicate_global_key_rejected()
    {
        await ResetAsync();
        await using (var system = NewScope(tenant: null))
        {
            await system.Store.CreateAsync(new PdfTemplate { Key = "dup", Body = "first", TenantId = null });
        }
        await using (var system = NewScope(tenant: null))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => system.Store.CreateAsync(new PdfTemplate { Key = "dup", Body = "second", TenantId = null }));
        }
    }
}
