using System.Text;

namespace Themia.ETax.Withholding;

/// <summary>A monthly withholding-return CSV (see <see cref="WithholdingFilingCsv"/>) and its control totals.</summary>
public sealed class WithholdingFilingCsvResult
{
    internal WithholdingFilingCsvResult(string csv, int rowCount, long paidSatang, long withheldSatang)
    {
        Csv = csv;
        RowCount = rowCount;
        PaidSatang = paidSatang;
        WithheldSatang = withheldSatang;
    }

    /// <summary>The CSV text: a header line, then one line per income row, each ended by CR LF.</summary>
    public string Csv { get; }

    /// <summary>The number of income rows (not certificates) in the file.</summary>
    public int RowCount { get; }

    /// <summary>The sum of the amounts paid, in satang: the figure to check against the return's total.</summary>
    public long PaidSatang { get; }

    /// <summary>The sum of the tax withheld, in satang: the figure to check against the return's total.</summary>
    public long WithheldSatang { get; }

    /// <summary>The CSV as UTF-8 with a byte order mark, so a spreadsheet reads the Thai text correctly.</summary>
    public byte[] ToUtf8Bytes() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Csv)).ToArray();
}
