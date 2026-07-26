using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Themia.AspNetCore.DataProtection.SqlServer;

/// <summary>SQL Server implementation of <see cref="IDataProtectionKeyDialect"/> (Microsoft.Data.SqlClient).</summary>
public sealed class SqlServerDataProtectionKeyDialect : IDataProtectionKeyDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    public SqlServerDataProtectionKeyDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new SqlConnection(connectionString);

    /// <inheritdoc />
    public string SelectAllSql => """SELECT [id] AS Id, [xml] AS Xml FROM [data_protection_keys] ORDER BY [id];""";

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO [data_protection_keys] ([friendly_name], [xml], [created_at])
        VALUES (@FriendlyName, @Xml, SYSUTCDATETIME());
        """;

    /// <inheritdoc />
    public string DeleteSql => """DELETE FROM [data_protection_keys] WHERE [id] = @Id;""";
}
