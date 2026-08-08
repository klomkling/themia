using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quartz;
using Themia.Data.Migrations;

namespace Themia.Scheduling.DependencyInjection;

/// <summary>Registers a persistent Quartz scheduler backed by AdoJobStore, with no ORM.</summary>
public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers a persistent Quartz scheduler over the <c>quartz</c> schema and starts it with the
    /// Quartz hosted service.
    /// </summary>
    /// <remarks>
    /// <b>The engine and connection string are explicit arguments, not discovered from DI.</b> The module
    /// this was split out of read them from <c>IDatabaseProvider</c>, which lives in
    /// <c>Themia.Framework.Data.EFCore</c> and takes a <c>DbContextOptionsBuilder</c> in its own contract
    /// — so a scheduler that only ever read a provider <em>name</em> off it dragged EF Core into every
    /// adopter's graph for a string (coord #0071). Passing the two values is the whole of what that
    /// dependency bought.
    /// <para>
    /// Call <see cref="SchedulingSchema.Migrate"/> before the scheduler starts: AdoJobStore expects the
    /// <c>qrtz_*</c> tables to exist and fails on its first operation if they do not.
    /// </para>
    /// <para>
    /// <b>The serializer and property handling are not defaults you may drop.</b> Quartz's own default
    /// serializer is Newtonsoft-based, and a great many examples still show it;
    /// <c>UseSystemTextJsonSerializer</c> is what keeps this package inside Themia's no-Newtonsoft rule.
    /// <c>UseProperties = true</c> stores the <c>JobDataMap</c> as string key-values, which is what makes
    /// a persisted job survive a type change in the host.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="engine">The database engine holding the <c>quartz</c> schema.</param>
    /// <param name="connectionString">The connection string AdoJobStore uses.</param>
    /// <param name="configure">Optional options configuration.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or empty.</exception>
    /// <exception cref="NotSupportedException"><paramref name="engine"/> is neither PostgreSQL nor SQL Server.</exception>
    public static IServiceCollection AddThemiaScheduling(
        this IServiceCollection services,
        MigrationEngine engine,
        string connectionString,
        Action<SchedulingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new SchedulingOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        if (!options.UsePersistentStore)
        {
            // Nothing is registered on purpose: the host supplies its own IScheduler. Registering a
            // scheduler here anyway would give it two, and the one the dashboard resolved would be
            // whichever DI happened to return.
            return services;
        }

        services.AddQuartz(q =>
        {
            q.SchedulerName = options.SchedulerName;

            q.SchedulerId = options.InstanceId;

            q.UsePersistentStore(store =>
            {
                store.UseProperties = true;
                store.UseSystemTextJsonSerializer();

                if (options.UseClustering)
                {
                    store.UseClustering();
                }

                switch (engine)
                {
                    case MigrationEngine.Postgres:
                        store.UsePostgres(ado =>
                        {
                            ado.ConnectionString = connectionString;
                            ado.TablePrefix = "quartz.qrtz_";
                        });
                        break;

                    case MigrationEngine.SqlServer:
                        store.UseSqlServer(ado =>
                        {
                            ado.ConnectionString = connectionString;

                            // UPPERCASE to match the verbatim Quartz SQL Server DDL, which creates
                            // [quartz].[QRTZ_*]. A case-insensitive collation forgives a lowercase
                            // prefix; a case-sensitive one answers "Invalid object name" at runtime.
                            // The schema itself is lowercase in both.
                            ado.TablePrefix = "quartz.QRTZ_";
                        });
                        break;

                    default:
                        throw new NotSupportedException(
                            $"Themia.Scheduling supports PostgreSQL and SQL Server; '{engine}' is not supported. "
                            + "The schema migrations carry the same restriction, so this would also have failed "
                            + "at migration time.");
                }
            });

            // A scheduler plugin rather than a bare job listener: the plugin's Initialize sets the
            // listener name and self-registers, and its Start resolves the execution-history store — the
            // lifecycle it is written for.
            q.SetProperty(
                "quartz.plugin.recentHistory.type",
                $"{typeof(Themia.Quartz.ExecutionHistoryPlugin).FullName}, "
                + $"{typeof(Themia.Quartz.ExecutionHistoryPlugin).Assembly.GetName().Name}");
        });

        if (options.UsePersistentExecutionHistory)
        {
            // RemoveAll + Add rather than TryAdd, and the reason is specific: AddThemiaQuartz registers
            // NO IExecutionHistoryStore at all — ExecutionHistoryPlugin falls back to
            // `new InProcExecutionHistoryStore()` when DI has none — but Themia.Modules.Scheduling
            // registers its EF store with TryAddSingleton. A host on the module that then opts into
            // persistence here would keep the EF store under TryAdd, and this option would do nothing
            // while reading as if it had. Last writer wins, explicitly.
            var factory = ConnectionFactory(engine, connectionString);
            services.RemoveAll<Themia.Quartz.IExecutionHistoryStore>();
            services.AddSingleton<Themia.Quartz.IExecutionHistoryStore>(sp =>
                new DapperExecutionHistoryStore(
                    factory,
                    sp.GetRequiredService<ILogger<DapperExecutionHistoryStore>>())
                {
                    SchedulerName = options.SchedulerName,
                });
        }

        services.AddQuartzHostedService(h => h.WaitForJobsToComplete = true);

        if (!options.UseClustering)
        {
            // Unconditional within this branch, because the unsafe state cannot be detected — see the
            // advisory's remarks. Clustering on is the supported configuration and says nothing.
            services.AddHostedService<UnclusteredPersistenceAdvisory>();
        }
        else if (!string.Equals(options.InstanceId, "AUTO", StringComparison.Ordinal))
        {
            // A duplicate instance id across nodes corrupts scheduler state rather than failing cleanly,
            // and no process can see another process's id. The configuration that PERMITS the fault is
            // the most that is observable from here, so that is what gets named.
            services.AddHostedService<ExplicitInstanceIdAdvisory>();
        }

        return services;
    }

    private static Func<DbConnection> ConnectionFactory(MigrationEngine engine, string connectionString) =>
        engine switch
        {
            MigrationEngine.Postgres => () => new NpgsqlConnection(connectionString),
            MigrationEngine.SqlServer => () => new SqlConnection(connectionString),
            _ => throw new NotSupportedException(
                $"Themia.Scheduling supports PostgreSQL and SQL Server; '{engine}' is not supported."),
        };
}
