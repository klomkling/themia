using System.Data.Common;

namespace Themia.Content.Tests;

/// <summary>A dialect that refuses to connect and counts attempts — proves a code path opens no connection. It says
/// nothing about SQL.</summary>
internal sealed class CountingContentDialect : IContentPageDialect
{
    public int ConnectionsRequested { get; private set; }

    public DbConnection CreateConnection()
    {
        ConnectionsRequested++;
        throw new InvalidOperationException("This test expected no connection to be opened.");
    }

    public bool IsDuplicateKey(DbException exception) => false;
    public string InsertPageSql => string.Empty;
    public string ImportPageSql => string.Empty;
    public string UpdatePageIfVersionSql => string.Empty;
    public string InsertRevisionSql => string.Empty;
    public string SelectPageSql => string.Empty;
    public string SelectPublishedSql => string.Empty;
    public string SelectRevisionSql => string.Empty;
    public string ListPagesSql => string.Empty;
    public string CountPagesSql => string.Empty;
    public string ListRevisionsSql => string.Empty;
    public string CountRevisionsSql => string.Empty;
}
