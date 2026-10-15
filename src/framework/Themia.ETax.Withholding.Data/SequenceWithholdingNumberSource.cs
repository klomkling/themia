using System.Data.Common;
using System.Globalization;
using Themia.ETax.Data;
using Themia.Framework.Data.Sequences;

namespace Themia.ETax.Withholding.Data;

/// <summary>
/// Gapless withholding certificate numbers over Themia's <see cref="ITransactionalSequenceAllocator"/>: the counter row is locked inside the
/// caller's issue transaction, so a rollback returns the number. One run per payer and period, keyed <c>DocNo:ETax:Withholding:{payer}[:{period}]</c>
/// with the Gregorian period; a <c>series</c> (the form's เล่มที่ as a short code) adds <c>:series={series}</c> and has its own run. The printed number has one of the three
/// <see cref="WithholdingNumberShape"/>s with a 6-digit running number (<c>WHT-2569-000123</c> by default; Buddhist-era year by default, Gregorian as an option).
/// </summary>
/// <remarks>Never use <c>ISequenceProvider</c> on these keys: it allocates on its own transaction and would open a gap.</remarks>
public sealed class SequenceWithholdingNumberSource : IWithholdingNumberSource
{
    private const int MaxKeyLength = 100;
    private const int SequenceDigits = 6;
    // The Buddhist-era year is year + 543 computed here, never from a Thai culture or calendar (their data can differ between hosts).
    private const int BuddhistEraOffset = 543;

    private readonly ITransactionalSequenceAllocator _allocator;
    private readonly WithholdingNumberOptions _options;

    /// <summary>Creates the source.</summary>
    /// <param name="allocator">The transactional sequence allocator.</param>
    /// <param name="options">The number format; validated here.</param>
    /// <exception cref="InvalidOperationException">The options are invalid.</exception>
    public SequenceWithholdingNumberSource(ITransactionalSequenceAllocator allocator, WithholdingNumberOptions options)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    /// <inheritdoc />
    public Task<string> NextNumberAsync(DbTransaction tx, string payerId, DateOnly issueDate, CancellationToken ct = default) =>
        NextNumberInSeriesAsync(tx, payerId, null, issueDate, ct);

    /// <inheritdoc />
    public async Task<string> NextNumberInSeriesAsync(DbTransaction tx, string payerId, string? series, DateOnly issueDate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentException.ThrowIfNullOrWhiteSpace(payerId);
        if (series is not null && (series.Length == 0 || series.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))))
            throw new ArgumentException("A series is 1 or more letters, digits, hyphens or underscores.", nameof(series));

        var period = KeyPeriod(issueDate);
        var key = $"DocNo:ETax:Withholding:{payerId}"
            + (series is null ? string.Empty : ":series=" + series)
            + (period.Length == 0 ? string.Empty : ":" + period);
        if (key.Length > MaxKeyLength)
        {
            throw new ArgumentException(
                $"The sequence key for this payer would be {key.Length} characters; the limit is {MaxKeyLength}. Use a shorter payer id or series.",
                nameof(payerId));
        }

        var next = _options.Scope == TaxDocumentNumberScope.Host
            ? await _allocator.NextHostInTransactionAsync(tx, key, ct).ConfigureAwait(false)
            : await _allocator.NextInTransactionAsync(tx, key, ct).ConfigureAwait(false);

        var counter = next.ToString("D" + SequenceDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var buddhist = _options.Year == WithholdingNumberYear.BuddhistEra;
        var year = (buddhist ? issueDate.Year + BuddhistEraOffset : issueDate.Year);
        return _options.Shape switch
        {
            WithholdingNumberShape.SeqSlashYear2 => $"{counter}/{(year % 100).ToString("D2", CultureInfo.InvariantCulture)}",
            WithholdingNumberShape.PrefixYearMonthSeq =>
                $"{_options.Prefix}-{(year % 100).ToString("D2", CultureInfo.InvariantCulture)}{issueDate.Month.ToString("D2", CultureInfo.InvariantCulture)}-{counter}",
            WithholdingNumberShape.YearSeq => $"{year.ToString("D4", CultureInfo.InvariantCulture)}-{counter}",
            _ => $"{_options.Prefix}-{year.ToString("D4", CultureInfo.InvariantCulture)}-{counter}",
        };
    }

    // The run key always uses the Gregorian period, whatever year the printed number shows.
    private string KeyPeriod(DateOnly date) => _options.Period switch
    {
        TaxDocumentNumberPeriod.Never => string.Empty,
        TaxDocumentNumberPeriod.Monthly => date.ToString("yyyyMM", CultureInfo.InvariantCulture),
        _ => date.ToString("yyyy", CultureInfo.InvariantCulture),
    };
}
