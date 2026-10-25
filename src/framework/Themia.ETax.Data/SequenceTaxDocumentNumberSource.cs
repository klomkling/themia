using System.Data.Common;
using System.Globalization;
using Themia.Framework.Data.Sequences;

namespace Themia.ETax.Data;

/// <summary>
/// Gapless document numbers for <see cref="TaxDocumentService"/>, drawn inside the caller's issue transaction from Themia's
/// <see cref="ITransactionalSequenceAllocator"/>: commit keeps the number, rollback returns it. One run per seller, kind and (optionally) period.
/// </summary>
/// <remarks>
/// The sequence key is <c>DocNo:ETax:&lt;kind&gt;:&lt;sellerId&gt;[:&lt;period&gt;]</c> and must fit the allocator's 100-character limit, so
/// keep seller ids short. Never call <c>ISequenceProvider.NextAsync</c> on these keys: it allocates in its own transaction and would break the
/// gapless run. The caller's transaction must be READ COMMITTED (the allocator requires it).
/// </remarks>
public sealed class SequenceTaxDocumentNumberSource : ITaxDocumentNumberSource
{
    private const int BuddhistEraOffset = 543;

    private readonly ITransactionalSequenceAllocator _allocator;
    private readonly TaxDocumentNumberOptions _options;

    /// <summary>Creates the source.</summary>
    /// <param name="allocator">The transactional allocator.</param>
    /// <param name="options">How numbers are formed.</param>
    public SequenceTaxDocumentNumberSource(ITransactionalSequenceAllocator allocator, TaxDocumentNumberOptions options)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<string> NextNumberAsync(
        DbTransaction tx, string sellerId, TaxDocumentKind kind, DateOnly issueDate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        var runPeriod = RunPeriodText(issueDate);
        var key = $"DocNo:ETax:{kind}:{sellerId}" + (runPeriod.Length == 0 ? string.Empty : ":" + runPeriod);
        if (key.Length > TaxDocumentNumberOptions.MaxKeyLength)
        {
            throw new ArgumentException(
                $"The sequence key for this seller would be {key.Length} characters; the limit is {TaxDocumentNumberOptions.MaxKeyLength}. Use a shorter seller id.",
                nameof(sellerId));
        }

        var next = _options.Scope == TaxDocumentNumberScope.Host
            ? await _allocator.NextHostInTransactionAsync(tx, key, ct).ConfigureAwait(false)
            : await _allocator.NextInTransactionAsync(tx, key, ct).ConfigureAwait(false);

        var counter = next.ToString("D" + _options.Padding.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var prefix = _options.Prefixes[kind];
        var period = PrintedPeriodText(issueDate);
        return period.Length == 0 ? $"{prefix}-{counter}" : $"{prefix}-{period}-{counter}";
    }

    // The run key always uses the Gregorian period, whatever year style the printed number shows: changing the style never starts a new run.
    private string RunPeriodText(DateOnly issueDate) => _options.Period switch
    {
        TaxDocumentNumberPeriod.Yearly => issueDate.ToString("yyyy", CultureInfo.InvariantCulture),
        TaxDocumentNumberPeriod.Monthly => issueDate.ToString("yyyyMM", CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private string PrintedPeriodText(DateOnly issueDate)
    {
        if (_options.YearStyle == TaxDocumentNumberYearStyle.Gregorian)
            return RunPeriodText(issueDate);

        // Buddhist-era year is year + 543 computed here, never from a Thai culture or calendar (their data can differ between hosts).
        var year = ((issueDate.Year + BuddhistEraOffset) % 100).ToString("D2", CultureInfo.InvariantCulture);
        return _options.Period switch
        {
            TaxDocumentNumberPeriod.Yearly => year,
            TaxDocumentNumberPeriod.Monthly => year + issueDate.Month.ToString("D2", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }
}
