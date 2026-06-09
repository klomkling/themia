using System.Data.Common;

namespace Themia.Framework.Data.Dapper.Connection;

/// <summary>
/// Engine seam: creates a (closed) connection. The Postgres package returns an NpgsqlConnection
/// using the tenant-resolved connection string.
/// </summary>
public interface IDapperConnectionFactory
{
    /// <summary>Creates a new, closed connection.</summary>
    DbConnection Create();
}
