using Dapper;

namespace Themia.AspNetCore.DataProtection;

/// <summary>Dapper-backed <see cref="IDataProtectionKeyStore"/> over an <see cref="IDataProtectionKeyDialect"/>.</summary>
public sealed class DataProtectionKeyStore : IDataProtectionKeyStore
{
    private readonly IDataProtectionKeyDialect dialect;

    /// <summary>Creates the store over <paramref name="dialect"/>.</summary>
    public DataProtectionKeyStore(IDataProtectionKeyDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        this.dialect = dialect;
    }

    /// <inheritdoc />
    public IReadOnlyList<DataProtectionKeyRecord> GetAll()
    {
        using var connection = dialect.CreateConnection();
        return connection.Query<DataProtectionKeyRecord>(dialect.SelectAllSql).AsList();
    }

    /// <inheritdoc />
    public void StoreXml(string? friendlyName, string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        using var connection = dialect.CreateConnection();
        connection.Execute(dialect.InsertSql, new { FriendlyName = friendlyName, Xml = xml });
    }

    /// <inheritdoc />
    public bool Delete(long id)
    {
        using var connection = dialect.CreateConnection();
        return connection.Execute(dialect.DeleteSql, new { Id = id }) > 0;
    }
}
