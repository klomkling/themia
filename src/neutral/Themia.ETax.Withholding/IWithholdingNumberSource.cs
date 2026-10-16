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

    /// <summary>
    /// Allocates and formats the next number of a SERIES: a short code (the form's เล่มที่ used as a series, e.g. <c>CR</c> or <c>SF</c>) with its
    /// own gapless run per payer and period. The default implementation serves only the no-series case and refuses a series, so an existing
    /// number source keeps compiling and fails loudly rather than mixing series into one run.
    /// </summary>
    /// <param name="tx">The caller's issue transaction. Never committed or rolled back here.</param>
    /// <param name="payerId">The issuing payer.</param>
    /// <param name="series">The series code, or <see langword="null"/> for the payer's unseries run.</param>
    /// <param name="issueDate">The date of issue, for a per-period run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException">This number source does not support series and <paramref name="series"/> is set.</exception>
    Task<string> NextNumberInSeriesAsync(DbTransaction tx, string payerId, string? series, DateOnly issueDate, CancellationToken ct = default) =>
        series is null
            ? NextNumberAsync(tx, payerId, issueDate, ct)
            : throw new NotSupportedException("This withholding number source does not support series (a book code with its own run).");
}
