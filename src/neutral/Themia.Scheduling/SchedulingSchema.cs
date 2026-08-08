using System.Reflection;
using Themia.Data.Migrations;

namespace Themia.Scheduling;

/// <summary>
/// The scheduling schema: the Quartz <c>qrtz_*</c> tables and Themia's own <c>scheduling</c> tables.
/// </summary>
/// <remarks>
/// Exposed so a host that already owns a FluentMigrator runner can scan
/// <see cref="Assembly"/> itself instead of taking Themia's runner — the shape ezy-assets asked for on
/// coord #0071, where the alternative was copying the Quartz DDL out of the upstream repository by hand
/// and owning it forever.
/// <para>
/// <b>PostgreSQL and SQL Server only.</b> Both migrations carry a branch that throws
/// <see cref="NotSupportedException"/> naming the active provider, so an unsupported engine fails at
/// migration time with something actionable rather than at the first scheduler operation.
/// </para>
/// </remarks>
public static class SchedulingSchema
{
    /// <summary>The assembly to scan for the scheduling migrations.</summary>
    public static Assembly Assembly { get; } = typeof(SchedulingSchema).Assembly;

    /// <summary>Creates or upgrades the scheduling schema.</summary>
    /// <param name="engine">The database engine.</param>
    /// <param name="connectionString">The connection string to migrate.</param>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or empty.</exception>
    public static void Migrate(MigrationEngine engine, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ThemiaMigrations.Run(engine, connectionString, Assembly);
    }
}
