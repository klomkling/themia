namespace Themia.Data.Migrations;

/// <summary>
/// The database engine whose FluentMigrator processor a migration run targets.
/// Neutral selector owned by this package (it cannot reference the framework's provider names).
/// </summary>
public enum MigrationEngine
{
    /// <summary>PostgreSQL (FluentMigrator <c>AddPostgres</c>).</summary>
    Postgres,

    /// <summary>MySQL / MariaDB (FluentMigrator <c>AddMySql8</c>).</summary>
    MySql,

    /// <summary>SQL Server (FluentMigrator <c>AddSqlServer</c>).</summary>
    SqlServer,
}
