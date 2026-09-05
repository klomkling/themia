namespace Themia.Framework.Data.Sequences;

/// <summary>The database engine a <see cref="ISequenceProvider"/> allocates against.</summary>
/// <remarks>
/// An enum rather than a per-engine package: the allocator binds to no ORM, so there is nothing to split
/// along. This mirrors <c>Themia.Data.Migrations</c>' <c>MigrationEngine</c>, which every Themia app
/// already references.
/// </remarks>
public enum SequenceEngine
{
    /// <summary>
    /// Not configured. Exists so that leaving <see cref="SequenceOptions.Engine"/> unset is REJECTED at
    /// registration rather than silently meaning PostgreSQL.
    /// </summary>
    /// <remarks>
    /// <c>Enum.IsDefined</c> returns true for the default of any enum whose first member is 0, so an
    /// engine at 0 makes "unset" indistinguishable from "explicitly chosen". An app on SQL Server that
    /// set only the connection string would then pass startup validation and fail at the first
    /// allocation, with the PostgreSQL driver parsing a SQL Server connection string — precisely the
    /// deferred failure eager validation exists to prevent.
    /// </remarks>
    Unspecified = 0,

    /// <summary>PostgreSQL.</summary>
    Postgres = 1,

    /// <summary>MySQL 8.0.13 or later. MariaDB is not supported.</summary>
    MySql = 2,

    /// <summary>Microsoft SQL Server.</summary>
    SqlServer = 3,
}
