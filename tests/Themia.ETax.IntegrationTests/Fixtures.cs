using System.Text;
using Themia.ETax;

namespace Themia.ETax.IntegrationTests;

internal static class Fixtures
{
    public static TaxDocumentSnapshot Receipt(string number = "TAX-2026-000001", int lines = 1, long amount = 100_000)
    {
        var items = Enumerable.Range(1, lines)
            .Select(i => new TaxDocumentLine
            {
                Description = $"รายการที่ {i}: Boost package สำหรับร้านค้า ระยะเวลาให้บริการหนึ่งเดือน — Café Łódź",
                AmountExVatSatang = amount,
            }).ToArray();
        var exVat = amount * lines;
        var vat = VatMath.AddVat(exVat, 700);
        return new TaxDocumentSnapshot
        {
            Kind = TaxDocumentKind.TaxInvoiceReceipt,
            DocumentNumber = number,
            IssueDate = new DateOnly(2026, 10, 14),
            Seller = new TaxParty { Name = "บริษัท ตัวอย่าง จำกัด", TaxId = "0105536078673", Branch = "00000", Address = "1 ถนนสุขุมวิท\nกรุงเทพมหานคร 10110" },
            Buyer = new TaxParty { Name = "คุณสมชาย ใจดี", TaxId = "1101700230708", Address = "99 หมู่ 1 ตำบลตัวอย่าง" },
            Lines = items,
            Totals = new TaxDocumentTotals
            {
                ExVatSatang = exVat,
                VatSatang = vat.VatSatang,
                InclVatSatang = vat.InclVatSatang,
                WithheldSatang = 0,
                NetReceivedSatang = vat.InclVatSatang,
                VatRateBasisPoints = 700,
            },
        };
    }

    public static byte[] Resource(string name)
    {
        using var stream = typeof(Fixtures).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static string DataUri(string mime, byte[] bytes) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    public static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
