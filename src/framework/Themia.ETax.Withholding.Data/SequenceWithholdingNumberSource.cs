using System.Data.Common;
using System.Globalization;
using Themia.ETax.Data;
using Themia.Framework.Data.Sequences;

namespace Themia.ETax.Withholding.Data;

/// <summary>
/// Gapless withholding certificate numbers over Themia's <see cref="ITransactionalSequenceAllocator"/>: the counter row is locked inside the
/// caller's issue transaction, so a rollback returns the number. One run per payer and period, keyed <c>DocNo:ETax:Withholding:{payer}[:{period}]</c>
/// with the Gregorian period; the printed number is <c>{prefix}-{year}-{counter}</c> (Buddhist-era year by default, Gregorian as an option).
/// </summary>
/// <remarks>Never use <c>ISequenceProvider</c> on these keys: it allocates on its own transaction and would open a gap.</remarks>
public sealed class SequenceWithholdingNumberSource : IWithholdingNumberSource
{
    private const int MaxKeyLength = 100;

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
    public async Task<string> NextNumberAsync(DbTransaction tx, string payerId, DateOnly issueDate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentException.ThrowIfNullOrWhiteSpace(payerId);

        var period = PeriodText(issueDate, buddhist: false);
        var key = $"DocNo:ETax:Withholding:{payerId}" + (period.Length == 0 ? string.Empty : ":" + period);
        if (key.Length > MaxKeyLength)
        {
            throw new ArgumentException(
                $"The sequence key for this payer would be {key.Length} characters; the limit is {MaxKeyLength}. Use a shorter payer id.",
                nameof(payerId));
        }

        var next = _options.Scope == TaxDocumentNumberScope.Host
            ? await _allocator.NextHostInTransactionAsync(tx, key, ct).ConfigureAwait(false)
            : await _allocator.NextInTransactionAsync(tx, key, ct).ConfigureAwait(false);

        var counter = next.ToString("D" + _options.Padding.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var printed = PeriodText(issueDate, buddhist: _options.Year == WithholdingNumberYear.BuddhistEra);
        return printed.Length == 0 ? $"{_options.Prefix}-{counter}" : $"{_options.Prefix}-{printed}-{counter}";
    }

    // The year is year + 543 computed here, never from a Thai culture or calendar (their data can differ between hosts).
    private string PeriodText(DateOnly date, bool buddhist)
    {
        if (_options.Period == TaxDocumentNumberPeriod.Never)
            return string.Empty;

        var year = (buddhist ? date.Year + 543 : date.Year).ToString("D4", CultureInfo.InvariantCulture);
        return _options.Period == TaxDocumentNumberPeriod.Monthly
            ? year + date.Month.ToString("D2", CultureInfo.InvariantCulture)
            : year;
    }
}
