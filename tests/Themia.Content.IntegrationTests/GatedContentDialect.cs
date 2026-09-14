using System.Data.Common;

namespace Themia.Content.IntegrationTests;

/// <summary>Wraps a real dialect and runs a hook when the service reads the insert-page or update-page SQL, which it
/// does immediately before executing that statement on a real connection.</summary>
internal sealed class GatedContentDialect(IContentPageDialect inner) : IContentPageDialect
{
    public Action? BeforeInsertPage { get; init; }

    public Action? BeforeUpdatePage { get; init; }

    public DbConnection CreateConnection() => inner.CreateConnection();

    public bool IsDuplicateKey(DbException exception) => inner.IsDuplicateKey(exception);

    public string InsertPageSql
    {
        get
        {
            BeforeInsertPage?.Invoke();
            return inner.InsertPageSql;
        }
    }

    public string ImportPageSql => inner.ImportPageSql;

    public string UpdatePageIfVersionSql
    {
        get
        {
            BeforeUpdatePage?.Invoke();
            return inner.UpdatePageIfVersionSql;
        }
    }

    public string InsertRevisionSql => inner.InsertRevisionSql;
    public string SelectPageSql => inner.SelectPageSql;
    public string SelectPublishedSql => inner.SelectPublishedSql;
    public string SelectRevisionSql => inner.SelectRevisionSql;
    public string ListPagesSql => inner.ListPagesSql;
    public string CountPagesSql => inner.CountPagesSql;
    public string ListRevisionsSql => inner.ListRevisionsSql;
    public string CountRevisionsSql => inner.CountRevisionsSql;
}
