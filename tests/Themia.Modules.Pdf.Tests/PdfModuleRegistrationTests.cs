using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore.Abstractions;
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
    public void Blank_connection_string_name_fails_options_validation()
    {
        var services = new ServiceCollection();
        services.AddThemiaPdfModuleEfCore(o => o.ConnectionStringName = " ");
        using var provider = services.BuildServiceProvider();

        // .Validate(...).ValidateOnStart() surfaces as OptionsValidationException on first .Value access.
        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<PdfModuleOptions>>().Value);
    }

    [Fact]
    public void EfCore_context_factory_rejects_unsupported_provider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Host=localhost;Database=x" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDatabaseProvider>(new FakeDatabaseProvider(DatabaseProviderNames.MySql));
        services.AddThemiaPdfModuleEfCore();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // The provider-selection switch runs while EF builds the (scoped) options, which happens when the
        // context is resolved from the scope — so the unsupported-provider guard surfaces on resolution.
        Assert.Throws<NotSupportedException>(() =>
            scope.ServiceProvider.GetRequiredService<PdfDbContext>());
    }

    [Fact]
    public void EfCore_store_resolves_under_scope_validation_with_scoped_tenant_context()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IDatabaseProvider>(new Themia.Framework.Data.EFCore.PostgreSql.PostgresDatabaseProvider());
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddSingleton<Themia.Framework.Data.Abstractions.Exceptions.ISqlExceptionInterpreter>(new FakeSqlExceptionInterpreter());
        services.AddThemiaPdfModuleEfCore();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
        using var scope = provider.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IPdfTemplateStore>(); // must NOT throw

        Assert.NotNull(store);
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

    private sealed class FakeSqlExceptionInterpreter : Themia.Framework.Data.Abstractions.Exceptions.ISqlExceptionInterpreter
    {
        public bool IsUniqueConstraintViolation(Exception? exception) => false;
    }

    private sealed class FakeDatabaseProvider(string providerName) : IDatabaseProvider
    {
        public string ProviderName { get; } = providerName;
        public void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider) { }
        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }
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
