using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Themia.Data.Migrations;
using Themia.Scheduling.DependencyInjection;
using Xunit;

namespace Themia.Scheduling.Tests;

/// <summary>
/// The EF-free half of the scheduling split (coord #0071). The module's own integration suite already
/// proves the persistent scheduler survives a restart and adopts an existing schema — and now runs that
/// proof through this package. What is asserted here is what only this package can be wrong about:
/// that it registers a scheduler without an ORM, and that the guards fire before anything reaches a
/// database.
/// </summary>
public class SchedulingRegistrationTests
{
    private const string ConnectionString = "Host=localhost;Database=unused";

    [Fact]
    public void AddThemiaScheduling_registers_a_scheduler_and_starts_it()
    {
        var services = new ServiceCollection();

        services.AddThemiaScheduling(MigrationEngine.Postgres, ConnectionString);

        Assert.Contains(services, d => d.ServiceType == typeof(ISchedulerFactory));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddThemiaScheduling_brings_no_entity_framework()
    {
        // The whole request, asserted against what actually RESTORES rather than against the compiled
        // assembly's references. An earlier version of this test read Assembly.GetReferencedAssemblies(),
        // which lists only what the compiler emitted a reference to — so adding the EF peer back to the
        // csproj left it green, because no code here uses an EF type. It caught a stray using-directive
        // and missed the package reference, which is the only thing anyone would actually re-add.
        //
        // The build output is the graph: if this package pulls EF, its assemblies land next to ours.
        var output = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileName)
            .Select(name => name!)
            .ToList();

        Assert.DoesNotContain(output, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(output, n => n.StartsWith("Npgsql.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(output, n => n.Equals("Themia.Framework.Data.EFCore.dll", StringComparison.OrdinalIgnoreCase));

        // And the assembly-reference check is kept as the cheaper, narrower guard it really is.
        var referenced = typeof(SchedulingSchema).Assembly.GetReferencedAssemblies().Select(a => a.Name!);
        Assert.DoesNotContain(referenced, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void AddThemiaScheduling_registers_nothing_when_the_host_supplies_its_own_scheduler()
    {
        // Registering one anyway would give the host two, and the dashboard would resolve whichever DI
        // happened to return — a coin flip that looks like a scheduler losing jobs.
        var services = new ServiceCollection();

        services.AddThemiaScheduling(MigrationEngine.Postgres, ConnectionString, o => o.UsePersistentStore = false);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISchedulerFactory));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void The_unclustered_advisory_is_registered_whenever_the_store_is_persistent()
    {
        // propertiezy's ask on #0071. Unconditional because the unsafe state cannot be detected: an
        // unclustered scheduler never writes a QRTZ_SCHEDULER_STATE row, and instanceId defaults to the
        // literal NON_CLUSTERED so every node would collide on one key anyway.
        var services = new ServiceCollection();
        services.AddThemiaScheduling(MigrationEngine.Postgres, ConnectionString);

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(UnclusteredPersistenceAdvisory));
    }

    [Fact]
    public void The_advisory_is_not_registered_when_no_scheduler_is()
    {
        // Warning about an unclustered persistent store the host does not have would be noise, and noise
        // is how a real warning stops being read.
        var services = new ServiceCollection();
        services.AddThemiaScheduling(MigrationEngine.Postgres, ConnectionString, o => o.UsePersistentStore = false);

        Assert.DoesNotContain(
            services,
            d => d.ImplementationType == typeof(UnclusteredPersistenceAdvisory));
    }

    [Theory]
    [InlineData(MigrationEngine.MySql)]
    public void An_unsupported_engine_is_refused_at_registration(MigrationEngine engine)
    {
        // The schema migrations carry the same restriction, so this would fail at migration time too —
        // but failing here means it fails before anything touches a database.
        var services = new ServiceCollection();

        Assert.Throws<NotSupportedException>(() => services.AddThemiaScheduling(engine, ConnectionString));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_connection_string_is_refused(string connectionString)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddThemiaScheduling(MigrationEngine.Postgres, connectionString));
    }

    [Fact]
    public void The_schema_assembly_carries_both_migrations()
    {
        // ezy-assets asked for the qrtz_* DDL as a scannable migration class so their own FluentMigrator
        // runner can take it — they said that alone would be most of the value, since the alternative is
        // copying the DDL out of the Quartz repository and owning it forever.
        var types = SchedulingSchema.Assembly.GetTypes().Select(t => t.Name).ToList();

        Assert.Contains("QuartzAdoJobStoreMigration", types);
        Assert.Contains("SchedulingSchemaMigration", types);
    }

    [Fact]
    public void Migrate_refuses_an_empty_connection_string_rather_than_reaching_a_runner()
    {
        Assert.Throws<ArgumentException>(() => SchedulingSchema.Migrate(MigrationEngine.Postgres, ""));
    }
}
