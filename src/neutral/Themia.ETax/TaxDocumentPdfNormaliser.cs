using System.Globalization;
using System.Text;

namespace Themia.ETax;

/// <summary>
/// Makes two renders of the same document byte-identical by pinning the only fields that differ: the PDF
/// creation and modification dates, which Chromium stamps with the wall clock.
/// </summary>
internal static class TaxDocumentPdfNormaliser
{
    /// <summary>Bumped whenever the normalisation changes, because that changes the hash.</summary>
    public const string Version = "n1";

    private static readonly byte[] CreationDateKey = Encoding.ASCII.GetBytes("/CreationDate (");
    private static readonly byte[] ModDateKey = Encoding.ASCII.GetBytes("/ModDate (");

    /// <summary>Returns a copy of <paramref name="pdf"/> whose date fields hold the issue date at 00:00:00 UTC.</summary>
    /// <exception cref="InvalidOperationException">No creation date is present, or a date does not have the expected length.</exception>
    public static byte[] NormaliseDates(byte[] pdf, DateOnly issueDate)
    {
        var value = Encoding.ASCII.GetBytes(
            $"D:{issueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}000000+00'00'");
        var copy = (byte[])pdf.Clone();

        if (Patch(copy, CreationDateKey, value) == 0)
            throw new InvalidOperationException("Themia.ETax: the PDF has no /CreationDate, so it cannot be normalised.");
        Patch(copy, ModDateKey, value);
        return copy;
    }

    private static int Patch(byte[] pdf, byte[] key, byte[] value)
    {
        var count = 0;
        for (var from = 0; from < pdf.Length;)
        {
            var at = pdf.AsSpan(from).IndexOf(key);
            if (at < 0)
                break;

            var start = from + at + key.Length;
            var length = pdf.AsSpan(start).IndexOf((byte)')');
            if (length != value.Length)
                throw new InvalidOperationException("Themia.ETax: a PDF date field does not have the expected length, so it cannot be normalised in place.");

            value.CopyTo(pdf, start);
            count++;
            from = start + length;
        }

        return count;
    }
}
