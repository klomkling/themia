using System.Data.Common;

namespace Themia.ETax.Withholding;

/// <summary>
/// Allocates the gapless certificate number inside the caller's issue transaction: if the transaction rolls back, the number is
/// returned. Formatting (prefix, year, padding) and the reset policy belong to the implementation.
/// </summary>
public interface IWithholdingNumberSource
{
    /// <summary>Allocates and formats the next number for <paramref name="payerId"/>.</summary>
    /// <param name="tx">The caller's issue transaction. Never committed or rolled back here.</param>
    /// <param name="payerId">The issuing payer.</param>
    /// <param name="issueDate">The date of issue, for a per-period run.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> NextNumberAsync(DbTransaction tx, string payerId, DateOnly issueDate, CancellationToken ct = default);
}
