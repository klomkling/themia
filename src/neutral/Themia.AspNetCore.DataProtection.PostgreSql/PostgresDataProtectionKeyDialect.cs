using System.Data.Common;
using Npgsql;

namespace Themia.AspNetCore.DataProtection.PostgreSql;

/// <summary>PostgreSQL implementation of <see cref="IDataProtectionKeyDialect"/> (Npgsql).</summary>
public sealed class PostgresDataProtectionKeyDialect : IDataProtectionKeyDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    public PostgresDataProtectionKeyDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public string SelectAllSql => """SELECT id AS Id, xml AS Xml FROM data_protection_keys ORDER BY id;""";

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO data_protection_keys (friendly_name, xml, created_at)
        VALUES (@FriendlyName, @Xml, now() AT TIME ZONE 'utc');
        """;

    /// <inheritdoc />
    public string DeleteSql => """DELETE FROM data_protection_keys WHERE id = @Id;""";
}
