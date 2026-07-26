using System.Data.Common;
using MySqlConnector;

namespace Themia.AspNetCore.DataProtection.MySql;

/// <summary>MySQL/MariaDB implementation of <see cref="IDataProtectionKeyDialect"/> (MySqlConnector).</summary>
public sealed class MySqlDataProtectionKeyDialect : IDataProtectionKeyDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    public MySqlDataProtectionKeyDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public string SelectAllXmlSql => """SELECT xml FROM data_protection_keys ORDER BY id;""";

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO data_protection_keys (friendly_name, xml, created_at)
        VALUES (@FriendlyName, @Xml, UTC_TIMESTAMP(6));
        """;
}
