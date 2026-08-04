namespace Themia.Data.Migrations;

/// <summary>
/// The database engine whose FluentMigrator processor a migration run targets.
/// Neutral selector owned by this package (it cannot reference the framework's provider names).
/// </summary>
public enum MigrationEngine
{
    /// <summary>PostgreSQL (FluentMigrator <c>AddPostgres</c>).</summary>
    Postgres,

    /// <summary>
    /// MySQL 8.0.13+ (FluentMigrator <c>AddMySql8</c>). This runner itself also works against MariaDB —
    /// <c>MigrationLockTests</c> proves the advisory-lock path on a real <c>mariadb:11</c> container, and
    /// <see cref="MigrationLock"/> is written to stay portable to it. That does <b>not</b> make MariaDB a
    /// supported engine for Themia as a whole: modules whose schema uses MySQL functional key parts
    /// (<c>Themia.Modules.Pdf</c>, <c>Themia.Challenges</c>) fail to migrate on it. See
    /// "Multi-database requirement" in <c>docs/themia-architecture-overview.md</c>.
    /// </summary>
    MySql,

    /// <summary>SQL Server (FluentMigrator <c>AddSqlServer</c>).</summary>
    SqlServer,
}
