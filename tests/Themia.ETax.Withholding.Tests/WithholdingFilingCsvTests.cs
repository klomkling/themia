using Xunit;

namespace Themia.ETax.Withholding.Tests;

// coord #0164 [17]: a CSV the accounting office can key a monthly PND3/PND53 return from. One file per form per month.
public class WithholdingFilingCsvTests
{
    private const string Header =
        "Sequence,Certificate number,Payee tax id,Payee name,Payee address,Date paid,Income type code,Income type,Amount paid,Rate percent,Tax withheld,Condition";

    private static WithholdingCertificateRecord Record(
        WithholdingCertificateSnapshot snapshot,
        WithholdingCertificateState state = WithholdingCertificateState.Issued)
    {
        var json = WithholdingSnapshotSerializer.Serialize(snapshot);
        return new WithholdingCertificateRecord
        {
            Id = Guid.NewGuid(),
            SourceEventId = snapshot.DocumentNumber,
            Revision = 1,
            PayerId = "payer-1",
            DocumentNumber = snapshot.DocumentNumber,
            IssueDate = snapshot.IssueDate,
            SnapshotJson = json,
            SnapshotSchemaVersion = snapshot.SchemaVersion,
            SnapshotSha256 = WithholdingSnapshotSerializer.ComputeHash(json),
            TemplateVersion = "1",
            State = state,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    private static WithholdingCertificateSnapshot With(
        string number, DateOnly datePaid, long paid = 1_000_000, int? rateBasisPoints = 300, long withheld = 30_000,
        WithholdingFilingForm form = WithholdingFilingForm.Pnd3, TaxParty? payee = null)
    {
        var sample = Samples.Valid();
        return sample with
        {
            DocumentNumber = number,
            FilingForm = form,
            Payee = payee ?? sample.Payee,
            Rows = [sample.Rows[0] with { DatePaid = datePaid, AmountPaidSatang = paid, RateBasisPoints = rateBasisPoints, TaxWithheldSatang = withheld }],
            Totals = new WithholdingTotals { PaidSatang = paid, WithheldSatang = withheld },
        };
    }

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void One_issued_certificate_becomes_one_row_in_the_form_and_month_it_was_paid_in()
    {
        var result = WithholdingFilingCsv.Build([Record(Samples.Valid())], WithholdingFilingForm.Pnd3, 2026, 10);

        var lines = Lines(result.Csv);
        Assert.Equal(Header, lines[0]);
        Assert.Equal(
            "1,WHT-2569-000123,1101700230708,สมชาย ใจดี,2 ถนนพระราม 4 กรุงเทพฯ,2026-10-14,fixture-fee,ค่าธรรมเนียม ค่านายหน้า,10000.00,3,300.00,WithheldAtSource",
            lines[1]);
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, result.RowCount);
        Assert.Equal(1_000_000, result.PaidSatang);
        Assert.Equal(30_000, result.WithheldSatang);
    }

    [Fact]
    public void Another_form_and_another_month_are_left_out()
    {
        var records = new[]
        {
            Record(Samples.Valid()),
            Record(With("WHT-2569-000124", new DateOnly(2026, 10, 3), form: WithholdingFilingForm.Pnd53)),
            Record(With("WHT-2569-000125", new DateOnly(2026, 11, 3))),
        };

        var result = WithholdingFilingCsv.Build(records, WithholdingFilingForm.Pnd3, 2026, 10);

        Assert.Equal(1, result.RowCount);
        Assert.DoesNotContain("000124", result.Csv, StringComparison.Ordinal);
        Assert.DoesNotContain("000125", result.Csv, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_certificate_is_left_out_so_its_replacement_is_not_counted_twice()
    {
        var records = new[]
        {
            Record(With("WHT-2569-000126", new DateOnly(2026, 10, 5)), WithholdingCertificateState.Cancelled),
            Record(With("WHT-2569-000127", new DateOnly(2026, 10, 5))),
        };

        var result = WithholdingFilingCsv.Build(records, WithholdingFilingForm.Pnd3, 2026, 10);

        Assert.Equal(1, result.RowCount);
        Assert.Contains("000127", result.Csv, StringComparison.Ordinal);
        Assert.DoesNotContain("000126", result.Csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_are_ordered_by_date_paid_then_certificate_number_and_numbered_from_one()
    {
        var records = new[]
        {
            Record(With("WHT-2569-000130", new DateOnly(2026, 10, 20))),
            Record(With("WHT-2569-000129", new DateOnly(2026, 10, 2))),
            Record(With("WHT-2569-000128", new DateOnly(2026, 10, 2))),
        };

        var lines = Lines(WithholdingFilingCsv.Build(records, WithholdingFilingForm.Pnd3, 2026, 10).Csv);

        Assert.StartsWith("1,WHT-2569-000128,", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("2,WHT-2569-000129,", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("3,WHT-2569-000130,", lines[3], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(300, "3")]
    [InlineData(150, "1.5")]
    [InlineData(1, "0.01")]
    [InlineData(1000, "10")]
    public void The_rate_prints_as_a_percent_without_trailing_zeros(int basisPoints, string expected)
    {
        var record = Record(With("WHT-2569-000131", new DateOnly(2026, 10, 5), rateBasisPoints: basisPoints));

        var cells = Lines(WithholdingFilingCsv.Build([record], WithholdingFilingForm.Pnd3, 2026, 10).Csv)[1].Split(',');

        Assert.Equal(expected, cells[^3]);
    }

    [Fact]
    public void A_row_with_no_rate_prints_an_empty_rate()
    {
        var record = Record(With("WHT-2569-000132", new DateOnly(2026, 10, 5), rateBasisPoints: null));

        var cells = Lines(WithholdingFilingCsv.Build([record], WithholdingFilingForm.Pnd3, 2026, 10).Csv)[1].Split(',');

        Assert.Equal("", cells[^3]);
    }

    [Fact]
    public void A_cell_with_a_comma_a_quote_or_a_line_break_is_quoted()
    {
        var payee = new TaxParty { Name = "A, \"B\"", TaxId = Samples.ValidPayeeTaxId, Address = "1 Road\nTown" };
        var record = Record(With("WHT-2569-000133", new DateOnly(2026, 10, 5), payee: payee));

        var csv = WithholdingFilingCsv.Build([record], WithholdingFilingForm.Pnd3, 2026, 10).Csv;

        Assert.Contains("\"A, \"\"B\"\"\",\"1 Road\nTown\"", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+66812345678")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    public void A_text_cell_that_a_spreadsheet_would_run_as_a_formula_is_defused_with_a_leading_quote(string name)
    {
        var payee = new TaxParty { Name = name, TaxId = Samples.ValidPayeeTaxId, Address = "x" };
        var record = Record(With("WHT-2569-000134", new DateOnly(2026, 10, 5), payee: payee));

        var csv = WithholdingFilingCsv.Build([record], WithholdingFilingForm.Pnd3, 2026, 10).Csv;

        Assert.DoesNotContain("," + name, csv, StringComparison.Ordinal);
        Assert.Contains("'" + name.Replace("\"", "\"\"", StringComparison.Ordinal), csv, StringComparison.Ordinal);
    }

    [Fact]
    public void No_certificate_gives_the_header_only_and_zero_totals()
    {
        var result = WithholdingFilingCsv.Build([], WithholdingFilingForm.Pnd53, 2026, 10);

        Assert.Equal(Header, Lines(result.Csv).Single());
        Assert.Equal(0, result.RowCount);
        Assert.Equal(0, result.PaidSatang);
        Assert.Equal(0, result.WithheldSatang);
    }

    [Fact]
    public void A_stored_snapshot_that_no_longer_matches_its_hash_is_refused_not_exported()
    {
        var tampered = Record(Samples.Valid()) with { SnapshotSha256 = new string('0', 64) };

        Assert.ThrowsAny<Exception>(() => WithholdingFilingCsv.Build([tampered], WithholdingFilingForm.Pnd3, 2026, 10));
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(0, 10)]
    public void A_month_or_year_out_of_range_is_refused(int year, int month)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WithholdingFilingCsv.Build([], WithholdingFilingForm.Pnd3, year, month));
    }

    [Fact]
    public void The_bytes_are_utf8_with_a_byte_order_mark_so_a_spreadsheet_reads_the_thai_text()
    {
        var bytes = WithholdingFilingCsv.Build([Record(Samples.Valid())], WithholdingFilingForm.Pnd3, 2026, 10).ToUtf8Bytes();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Contains("สมชาย ใจดี", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }
}
