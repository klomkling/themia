using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Pdf;
using Themia.Modules.Pdf.Store;
using Xunit;

namespace Themia.Modules.Pdf.IntegrationTests;

/// <summary>Peer-agnostic conformance facts exercised against a real database by each data-layer peer
/// (EF Core, Dapper). A peer supplies its wiring via <see cref="ConfigurePeer"/> and a reset hook; the
/// same twelve facts run for both, verifying the migration, both stores, tenant/global resolution, the
/// tenant/global write-asymmetry, and owned-row scoping of writes on a real engine.</summary>
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
        await using (var acme = NewScope(new TenantId("acme")))
        {
            var all = await acme.Store.ListAsync();
            Assert.Contains(all, t => t.Key == "x" && t.TenantId is not null);
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

    [Fact]
    public async Task Recreate_key_after_soft_delete_succeeds()
    {
        await ResetAsync();
        await using var acme = NewScope(new TenantId("acme"));
        var created = await acme.Store.CreateAsync(new PdfTemplate { Key = "doc", Body = "v1" });
        await acme.Store.DeleteAsync(created.Id);

        await acme.Store.CreateAsync(new PdfTemplate { Key = "doc", Body = "v2" });

        var resolved = await acme.Store.ResolveAsync("doc");
        Assert.Equal("v2", resolved.Body);
    }

    [Fact]
    public async Task Update_changes_content_of_owned_template()
    {
        await ResetAsync();
        PdfTemplate created;
        await using (var acme = NewScope(new TenantId("acme")))
        {
            created = await acme.Store.CreateAsync(new PdfTemplate { Key = "u", Body = "v1" });
        }
        await using (var acme = NewScope(new TenantId("acme")))
        {
            // Reuse the created instance to obtain its Id (Id has only an internal setter); mutate content.
            created.Body = "v2";
            await acme.Store.UpdateAsync(created);
        }
        await using (var acme = NewScope(new TenantId("acme")))
        {
            var fetched = await acme.Store.GetAsync(created.Id);
            Assert.Equal("v2", fetched?.Body);
        }
    }

    [Fact]
    public async Task Update_of_row_outside_scope_throws()
    {
        await ResetAsync();
        PdfTemplate g;
        await using (var system = NewScope(tenant: null))
        {
            g = await system.Store.CreateAsync(new PdfTemplate { Key = "g", Body = "G", TenantId = null });
        }
        await using (var acme = NewScope(new TenantId("acme")))
        {
            g.Body = "hacked";
            await Assert.ThrowsAsync<TemplateNotFoundException>(() => acme.Store.UpdateAsync(g));
        }
        await using (var system = NewScope(tenant: null))
        {
            var fetched = await system.Store.GetAsync(g.Id);
            Assert.Equal("G", fetched?.Body); // untouched by the cross-scope update attempt
        }
    }

    [Fact]
    public async Task Delete_of_row_outside_scope_is_noop()
    {
        await ResetAsync();
        PdfTemplate g2;
        await using (var system = NewScope(tenant: null))
        {
            g2 = await system.Store.CreateAsync(new PdfTemplate { Key = "g2", Body = "G2", TenantId = null });
        }
        await using (var acme = NewScope(new TenantId("acme")))
        {
            await acme.Store.DeleteAsync(g2.Id); // must not throw; a cross-scope target is a no-op
        }
        await using (var system = NewScope(tenant: null))
        {
            Assert.NotNull(await system.Store.GetAsync(g2.Id)); // still present
        }
    }

    [Fact]
    public async Task Create_with_foreign_tenant_is_rejected()
    {
        await ResetAsync();
        await using var acme = NewScope(new TenantId("acme"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => acme.Store.CreateAsync(new PdfTemplate { Key = "f", Body = "v", TenantId = new TenantId("victim") }));
    }

    [Fact]
    public async Task System_scope_cannot_create_tenant_owned_row()
    {
        await ResetAsync();
        await using var system = NewScope(tenant: null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => system.Store.CreateAsync(new PdfTemplate { Key = "s", Body = "v", TenantId = new TenantId("acme") }));
    }
}
