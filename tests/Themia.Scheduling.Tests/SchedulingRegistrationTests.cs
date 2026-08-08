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

    [Fact]
    public void Clustering_on_silences_the_unclustered_warning()
    {
        // The warning names an unsupported configuration. Once clustering is on, multi-instance is the
        // supported one and there is nothing to say — a warning that fires when everything is correct is
        // how a real warning stops being read.
        var services = new ServiceCollection();
        services.AddThemiaScheduling(MigrationEngine.Postgres, ConnectionString, o => o.UseClustering = true);

        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(UnclusteredPersistenceAdvisory));
    }

    [Fact]
    public void Clustering_with_the_default_instance_id_warns_about_nothing()
    {
        var services = new ServiceCollection();
        services.AddThemiaScheduling(MigrationEngine.Postgres, ConnectionString, o => o.UseClustering = true);

        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(ExplicitInstanceIdAdvisory));
    }

    [Fact]
    public void Clustering_with_an_explicit_instance_id_warns()
    {
        // The duplicate itself is undetectable — no process can see another process's instance id — so
        // what gets named is the configuration that permits it.
        var services = new ServiceCollection();
        services.AddThemiaScheduling(
            MigrationEngine.Postgres, ConnectionString,
            o => { o.UseClustering = true; o.InstanceId = "node-1"; });

        Assert.Contains(services, d => d.ImplementationType == typeof(ExplicitInstanceIdAdvisory));
    }

    [Fact]
    public void InstanceId_defaults_to_AUTO()
    {
        // AUTO is what makes a duplicate id unrepresentable rather than a deployment detail somebody has
        // to get right on every node forever.
        Assert.Equal("AUTO", new SchedulingOptions().InstanceId);
    }

    [Fact]
    public void Clustering_is_off_by_default()
    {
        // Defaulting it on would add lock contention to every existing single-instance adopter on
        // upgrade, with no diff on their side and nothing failing.
        Assert.False(new SchedulingOptions().UseClustering);
    }

    [Fact]
    public void Persistent_execution_history_is_off_by_default()
    {
        // Adopting the package must not silently start writing rows to a schema the host never asked
        // for. /admin/jobs keeps behaving exactly as it does today unless someone opts in.
        var services = new ServiceCollection();
        services.AddThemiaScheduling(MigrationEngine.Postgres, ConnectionString);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Themia.Quartz.IExecutionHistoryStore));
        Assert.False(new SchedulingOptions().UsePersistentExecutionHistory);
    }

    [Fact]
    public void Opting_in_replaces_the_in_memory_store()
    {
        // Falsifiable only against a COMPETING registration. AddThemiaQuartz registers no store at all
        // (the plugin news up an in-proc one when DI has none), so an earlier version of this test could
        // not tell TryAdd from Add — it passed either way. What this has to beat is
        // Themia.Modules.Scheduling's EF store, registered with TryAddSingleton; stand one in.
        var services = new ServiceCollection();
        services.AddSingleton<Themia.Quartz.IExecutionHistoryStore>(new Themia.Quartz.InProcExecutionHistoryStore());

        services.AddThemiaScheduling(
            MigrationEngine.Postgres, ConnectionString, o => o.UsePersistentExecutionHistory = true);

        var registered = services.Where(d => d.ServiceType == typeof(Themia.Quartz.IExecutionHistoryStore)).ToList();
        Assert.Single(registered);
        var store = registered[0].ImplementationFactory!(new ServiceCollection().AddLogging().BuildServiceProvider());
        Assert.IsType<DapperExecutionHistoryStore>(store);
    }
}
