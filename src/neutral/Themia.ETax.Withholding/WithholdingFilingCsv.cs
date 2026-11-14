using System.Globalization;
using System.Text;

namespace Themia.ETax.Withholding;

/// <summary>
/// Builds the CSV an accounting office keys a monthly PND3 / PND53 return from (coord #0164): one file per form per month, one line per
/// income row. It is not the Revenue Department's e-filing layout. Only Issued certificates count, so a cancelled one and its replacement are
/// never both in the file. Every snapshot is checked against its stored hash before it is read.
/// </summary>
public static class WithholdingFilingCsv
{
    private const string Header =
        "Sequence,Certificate number,Payee tax id,Payee name,Payee address,Date paid,Income type code,Income type,Amount paid,Rate percent,Tax withheld,Condition";
    private const string LineEnd = "\r\n";
    private const int SatangPerBaht = 100;
    private const int BasisPointsPerPercent = 100;
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@', '\t', '\r', '\n'];

    /// <summary>Builds the CSV of <paramref name="form"/> for the month of <paramref name="year"/>/<paramref name="month"/>, by the date each row was paid.</summary>
    /// <param name="certificates">The payer's stored certificates; the caller has already scoped them to one payer.</param>
    /// <param name="form">The return: Pnd3 or Pnd53 (any form is accepted).</param>
    /// <param name="year">The Gregorian year of the payment date.</param>
    /// <param name="month">The month, 1 to 12.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="year"/> or <paramref name="month"/> is out of range.</exception>
    public static WithholdingFilingCsvResult Build(IEnumerable<WithholdingCertificateRecord> certificates, WithholdingFilingForm form, int year, int month)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);

        var lines = certificates
            .Where(c => c.State == WithholdingCertificateState.Issued)
            .Select(c => WithholdingSnapshotSerializer.Deserialize(c.SnapshotJson, c.SnapshotSha256))
            .Where(s => s.FilingForm == form)
            .SelectMany(s => s.Rows.Select((row, index) => (Snapshot: s, Row: row, Index: index)))
            .Where(x => x.Row.DatePaid.Year == year && x.Row.DatePaid.Month == month)
            .OrderBy(x => x.Row.DatePaid)
            .ThenBy(x => x.Snapshot.DocumentNumber, StringComparer.Ordinal)
            .ThenBy(x => x.Index)
            .ToList();

        var csv = new StringBuilder(Header).Append(LineEnd);
        for (var i = 0; i < lines.Count; i++)
        {
            AppendLine(csv, i + 1, lines[i].Snapshot, lines[i].Row);
        }

        return new WithholdingFilingCsvResult(
            csv.ToString(), lines.Count, lines.Sum(x => x.Row.AmountPaidSatang), lines.Sum(x => x.Row.TaxWithheldSatang));
    }

    private static void AppendLine(StringBuilder csv, int sequence, WithholdingCertificateSnapshot snapshot, WithholdingIncomeRow row)
    {
        string[] cells =
        [
            sequence.ToString(CultureInfo.InvariantCulture),
            Text(snapshot.DocumentNumber),
            Text(snapshot.Payee.TaxId),
            Text(snapshot.Payee.Name),
            Text(snapshot.Payee.Address),
            row.DatePaid.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Text(row.IncomeTypeCode),
            Text(row.LabelTh),
            Baht(row.AmountPaidSatang),
            row.RateBasisPoints is { } basisPoints ? Percent(basisPoints) : string.Empty,
            Baht(row.TaxWithheldSatang),
            snapshot.Condition.ToString(),
        ];
        csv.Append(string.Join(',', cells)).Append(LineEnd);
    }

    private static string Baht(long satang) =>
        ((decimal)satang / SatangPerBaht).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Percent(int basisPoints) =>
        ((decimal)basisPoints / BasisPointsPerPercent).ToString("0.##", CultureInfo.InvariantCulture);

    // A text cell that starts like a formula gets a leading apostrophe (CSV injection), then RFC 4180 quoting when it needs it.
    private static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOfAny(FormulaPrefixes, 0, 1) == 0)
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
