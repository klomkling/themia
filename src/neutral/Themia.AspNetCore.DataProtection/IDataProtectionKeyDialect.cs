using System.Data.Common;

namespace Themia.AspNetCore.DataProtection;

/// <summary>
/// Per-engine knowledge for the Data Protection key store: how to open a connection, and the statements the
/// key ring needs. One schema serves all engines, so only the quoting and the server-clock function differ.
/// </summary>
public interface IDataProtectionKeyDialect
{
    /// <summary>Creates a new, unopened connection to the key store.</summary>
    DbConnection CreateConnection();

    /// <summary>Reads every stored element as <c>id</c> and <c>xml</c>, oldest first.</summary>
    string SelectAllSql { get; }

    /// <summary>
    /// Appends one element. Must set the creation timestamp from the <em>server</em> clock, not a parameter —
    /// application servers in a fleet disagree, and the key ring is shared across all of them.
    /// </summary>
    string InsertSql { get; }

    /// <summary>Deletes the row whose <c>id</c> is <c>@Id</c>.</summary>
    string DeleteSql { get; }
}
