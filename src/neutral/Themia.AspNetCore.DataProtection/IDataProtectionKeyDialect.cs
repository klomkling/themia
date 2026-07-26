using System.Data.Common;

namespace Themia.AspNetCore.DataProtection;

/// <summary>
/// Per-engine knowledge for the Data Protection key store: how to open a connection, and the two statements
/// the key ring needs. One schema serves all engines, so only the quoting and the server-clock function differ.
/// </summary>
public interface IDataProtectionKeyDialect
{
    /// <summary>Creates a new, unopened connection to the key store.</summary>
    DbConnection CreateConnection();

    /// <summary>Reads every stored key element as raw XML, oldest first.</summary>
    string SelectAllXmlSql { get; }

    /// <summary>
    /// Appends one key element. Must set the creation timestamp from the <em>server</em> clock, not a
    /// parameter — application servers in a fleet disagree, and the key ring is shared across all of them.
    /// </summary>
    string InsertSql { get; }
}
