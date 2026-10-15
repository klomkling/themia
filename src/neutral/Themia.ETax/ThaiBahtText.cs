using System.Text;

namespace Themia.ETax;

/// <summary>
/// Spells an amount of money in Thai words (จำนวนเงินตัวอักษร), as printed under the totals of a withholding certificate and, in a future
/// template version, on receipts, tax invoices and credit notes. Pure: no culture, no clock.
/// </summary>
public static class ThaiBahtText
{
    private const string Zero = "ศูนย์";
    private const string Baht = "บาท";
    private const string Satang = "สตางค์";
    private const string Exact = "ถ้วน";
    private const string Million = "ล้าน";
    private const string Eleven = "เอ็ด";
    private const string Twenty = "ยี่";
    private const string Ten = "สิบ";
    private const long SatangPerBaht = 100;
    private const long MillionValue = 1_000_000;

    private static readonly string[] Digits = ["", "หนึ่ง", "สอง", "สาม", "สี่", "ห้า", "หก", "เจ็ด", "แปด", "เก้า"];
    private static readonly int[] PlaceDivisors = [1, 10, 100, 1_000, 10_000, 100_000];
    private static readonly string[] Places = ["", Ten, "ร้อย", "พัน", "หมื่น", "แสน"];

    /// <summary>E.g. 123456 satang → "หนึ่งพันสองร้อยสามสิบสี่บาทห้าสิบหกสตางค์"; no satang → "…บาทถ้วน"; under one baht omits the baht part.</summary>
    /// <param name="satang">The amount in satang.</param>
    /// <param name="etMode">When a units digit of 1 is read "เอ็ด" rather than "หนึ่ง"; both conventions are in use.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="satang"/> is negative or <paramref name="etMode"/> is undefined.</exception>
    public static string Format(long satang, ThaiBahtTextEtMode etMode = ThaiBahtTextEtMode.Always)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(satang);
        if (!Enum.IsDefined(etMode))
            throw new ArgumentOutOfRangeException(nameof(etMode), etMode, "Unknown เอ็ด convention.");

        var baht = satang / SatangPerBaht;
        var fraction = satang % SatangPerBaht;
        if (baht == 0 && fraction == 0)
            return Zero + Baht + Exact;

        var text = new StringBuilder();
        if (baht > 0)
            text.Append(Number(baht, etMode)).Append(Baht);
        if (fraction == 0)
            return text.Append(Exact).ToString();

        return text.Append(Number(fraction, etMode)).Append(Satang).ToString();
    }

    private static string Number(long value, ThaiBahtTextEtMode etMode)
    {
        var low = value % MillionValue;
        var high = value / MillionValue;
        if (high == 0)
            return Group((int)low, hasHigherGroup: false, etMode);

        var text = Number(high, etMode) + Million;
        return low == 0 ? text : text + Group((int)low, hasHigherGroup: true, etMode);
    }

    // 1..999999. A units digit of 1 reads "เอ็ด" after any higher digit or higher group in Always mode ("สิบเอ็ด", "หนึ่งร้อยเอ็ด", "หนึ่งล้านเอ็ด"),
    // only after a tens digit in TensOnly mode ("สิบเอ็ด", but "หนึ่งร้อยหนึ่ง", "หนึ่งล้านหนึ่ง"), else "หนึ่ง".
    private static string Group(int value, bool hasHigherGroup, ThaiBahtTextEtMode etMode)
    {
        var text = new StringBuilder();
        for (var place = Places.Length - 1; place >= 0; place--)
        {
            var digit = value / PlaceDivisors[place] % 10;
            if (digit == 0)
                continue;

            var followsHigherDigit = etMode == ThaiBahtTextEtMode.Always
                ? hasHigherGroup || value >= 10
                : value / 10 % 10 > 0;
            text.Append(DigitWord(digit, place, followsHigherDigit));
            text.Append(Places[place]);
        }

        return text.ToString();
    }

    private static string DigitWord(int digit, int place, bool followsHigherDigit) => (place, digit) switch
    {
        (1, 1) => string.Empty,
        (1, 2) => Twenty,
        (0, 1) when followsHigherDigit => Eleven,
        _ => Digits[digit],
    };
}
