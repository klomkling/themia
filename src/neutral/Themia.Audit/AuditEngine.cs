namespace Themia.Audit;

/// <summary>
/// The database engine a <c>Themia.Audit</c> store targets.
/// </summary>
/// <remarks>
/// <see cref="Unspecified"/> reserves the enum's default value (<c>0</c>), for the same reason as
/// <see cref="AuditCategory.Unspecified"/> and <see cref="AuditOutcome.Unspecified"/>.
/// </remarks>
public enum AuditEngine
{
    /// <summary>Not set.</summary>
    Unspecified = 0,

    /// <summary>PostgreSQL.</summary>
    Postgres,

    /// <summary>Microsoft SQL Server.</summary>
    SqlServer,

    /// <summary>MySQL 8.0.13 or later. MariaDB is not supported.</summary>
    MySql,
}
