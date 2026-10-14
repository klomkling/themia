using System.Data.Common;

namespace Themia.ETax;

/// <summary>
/// Allocates the gapless document number inside the caller's issue transaction: if the transaction rolls back, the number
/// is returned. Formatting (prefix, year, padding) and the reset policy belong to the app.
/// </summary>
public interface ITaxDocumentNumberSource
{
    /// <summary>Allocates and formats the next number for <paramref name="sellerId"/> and <paramref name="kind"/>.</summary>
    /// <param name="tx">The caller's issue transaction. Never committed or rolled back here.</param>
    /// <param name="sellerId">The issuing seller.</param>
    /// <param name="kind">The document kind.</param>
    /// <param name="issueDate">The tax point, for a per-period run.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> NextNumberAsync(DbTransaction tx, string sellerId, TaxDocumentKind kind, DateOnly issueDate, CancellationToken ct = default);
}
