using System.Data.Common;

namespace Themia.ETax.Testing;

/// <summary>
/// What the conformance tests need from an app: a store on a fresh, empty database and a way to open the
/// READ COMMITTED transaction the issue path runs in. Disposing it throws the database away.
/// </summary>
public interface ITaxDocumentStoreHarness : IAsyncDisposable
{
    /// <summary>The store under test.</summary>
    ITaxDocumentStore Store { get; }

    /// <summary>Opens a READ COMMITTED transaction on the store's database. The tests commit or roll it back.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<DbTransaction> BeginTransactionAsync(CancellationToken ct = default);
}
