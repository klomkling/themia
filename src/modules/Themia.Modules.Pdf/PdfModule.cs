using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Pdf.Migrations;

namespace Themia.Modules.Pdf;

/// <summary>The PDF module: runs the FluentMigrator schema for the tenant-aware template store. The host
/// wires the services via <c>AddThemiaPdfModuleEfCore(...)</c> or <c>AddThemiaPdfModuleDapper(...)</c>;
/// this module exists for hosts that drive modules through the <see cref="IThemiaModule"/> convention.</summary>
public sealed class PdfModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly PdfModuleOptions options;

    /// <summary>Creates the module for the given migration engine with default options.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    public PdfModule(MigrationEngine engine) : this(engine, new PdfModuleOptions()) { }

    /// <summary>Creates the module for the given migration engine and options.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    /// <param name="options">The module options.</param>
    public PdfModule(MigrationEngine engine, PdfModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Pdf",
        displayName: "PDF Templates",
        description: "Tenant-aware PDF/HTML template store with global-default fallback and render-by-key.",
        version: new Version(0, 7, 0, 0));

    /// <inheritdoc />
    /// <remarks>
    /// The schema migration runs synchronously via <c>ThemiaMigrations.Run</c> (FluentMigrator's runner is
    /// synchronous), so <paramref name="cancellationToken"/> is honored at the boundary but cannot interrupt
    /// an in-flight <c>MigrateUp</c>.
    /// </remarks>
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var configuration = sp.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the PDF module requires it.");

        ThemiaMigrations.Run(engine, connectionString, typeof(PdfTemplateSchemaMigration).Assembly);

        return ValueTask.CompletedTask;
    }
}
