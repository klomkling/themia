namespace Themia.Framework.Data.Sequences;

/// <summary>The database engine a <see cref="ISequenceProvider"/> allocates against.</summary>
/// <remarks>
/// An enum rather than a per-engine package: the allocator binds to no ORM, so there is nothing to split
/// along. This mirrors <c>Themia.Data.Migrations</c>' <c>MigrationEngine</c>, which every Themia app
/// already references.
/// </remarks>
public enum SequenceEngine
{
    /// <summary>PostgreSQL.</summary>
    Postgres = 0,

    /// <summary>MySQL 8.0.13 or later. MariaDB is not supported.</summary>
    MySql = 1,

    /// <summary>Microsoft SQL Server.</summary>
    SqlServer = 2,
}
