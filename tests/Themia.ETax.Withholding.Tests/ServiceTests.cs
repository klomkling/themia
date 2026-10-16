using Xunit;

namespace Themia.ETax.Withholding.Tests;

public class ServiceTests
{
    private static WithholdingCertificateSnapshot Replacing(Guid replacedId, string number, DateOnly? issued = null) => Samples.Valid() with
    {
        Replacement = new TaxDocumentReplacement
        {
            ReplacesDocumentId = replacedId,
            OriginalDocumentNumber = number,
            OriginalIssueDate = issued ?? new DateOnly(2026, 10, 15),
            Reason = "ชื่อผิด",
        },
    };

    [Fact]
    public async Task Issue_ShouldStoreTheNumberedSnapshotAndItsHash_InTheCallersTransaction()
    {
        var flow = new Flow();

        var result = await flow.IssueAsync(Flow.Request("pay_1"));

        Assert.Equal(WithholdingIssueOutcome.Issued, result.Outcome);
        var row = flow.Row(result.DocumentId!.Value);
        Assert.Equal("WHT-2569-000001", row.DocumentNumber);
        Assert.Equal(Flow.Payer, row.PayerId);
        Assert.Equal("pay_1", row.SourceEventId);
        Assert.Equal(1, row.Revision);
        Assert.Equal(WithholdingCertificateState.Issued, row.State);
        Assert.Equal(WithholdingTemplates.CurrentVersion, row.TemplateVersion);
        Assert.Equal(flow.Clock.GetUtcNow(), row.CreatedAt);
        var stored = WithholdingSnapshotSerializer.Deserialize(row.SnapshotJson, row.SnapshotSha256);
        Assert.Equal("WHT-2569-000001", stored.DocumentNumber);
        Assert.Equal(row.IssueDate, stored.IssueDate);
        Assert.Equal(WithholdingCertificateSnapshot.CurrentSchemaVersion, row.SnapshotSchemaVersion);
        Assert.Empty(WithholdingSnapshotValidator.Validate(stored, Samples.Catalogue()));
    }

    [Fact]
    public async Task Issue_ShouldIgnoreTheNumberTheCallerPutInTheSnapshot()
    {
        var flow = new Flow();
        var id = await flow.IssuedAsync("pay_1", Samples.Valid() with { DocumentNumber = "MINE-1" });

        Assert.Equal("WHT-2569-000001", flow.Row(id).DocumentNumber);
    }

    [Fact]
    public async Task Issue_ShouldAnswerAlreadyIssued_WithoutConsumingANumber_WhenTheKeyExists()
    {
        var flow = new Flow();
        var id = await flow.IssuedAsync("pay_1");

        var again = await flow.IssueAsync(Flow.Request("pay_1"));

        Assert.Equal(WithholdingIssueOutcome.AlreadyIssued, again.Outcome);
        Assert.Equal(id, again.DocumentId);
        Assert.Equal(1, flow.Numbers.Calls);
        Assert.Single(flow.Store.Rows);
    }

    [Fact]
    public async Task Issue_ShouldAnswerSourceConflict_WithoutLeakingTheOtherPayersId_WhenTheKeyBelongsToAnotherPayer()
    {
        var flow = new Flow();
        await flow.IssuedAsync("pay_1");

        var result = await flow.IssueAsync(Flow.Request("pay_1", payer: "payer-2"));

        Assert.Equal(WithholdingIssueOutcome.SourceConflict, result.Outcome);
        Assert.Null(result.DocumentId);
        Assert.Equal(1, flow.Numbers.Calls);
    }

    [Fact]
    public async Task Issue_ShouldFailValidation_WithoutConsumingANumberOrStoringAnything()
    {
        var flow = new Flow();
        var bad = Samples.Valid() with { Payee = Samples.Valid().Payee with { TaxId = "123" } };

        var result = await flow.IssueAsync(Flow.Request("pay_1", bad));

        Assert.Equal(WithholdingIssueOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "payee.tax_id"), result.Errors);
        Assert.Equal(0, flow.Numbers.Calls);
        Assert.Empty(flow.Store.Rows);
    }

    [Fact]
    public async Task Issue_ShouldValidateAgainstTheCatalogue()
    {
        var flow = new Flow();
        var result = await flow.IssueAsync(Flow.Request("pay_1", Samples.Valid() with { IncomeCatalogueVersion = "old" }));

        Assert.Equal(WithholdingIssueOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.CatalogueMismatch, "income_catalogue_version"), result.Errors);
    }

    [Fact]
    public async Task Issue_ShouldHonourTheChecksumOption()
    {
        var checksumOff = new Flow(new WithholdingValidationOptions { ValidateTaxIdChecksum = false });
        var bad = Samples.Valid() with { Payee = Samples.Valid().Payee with { TaxId = "1101700230700" } };

        Assert.Equal(WithholdingIssueOutcome.Issued, (await checksumOff.IssueAsync(Flow.Request("pay_1", bad))).Outcome);
        Assert.Equal(WithholdingIssueOutcome.ValidationFailed, (await new Flow().IssueAsync(Flow.Request("pay_1", bad))).Outcome);
    }

    [Fact]
    public async Task Issue_ShouldReportAMalformedSnapshot_NotThrow_WhenARequiredPartIsMissing()
    {
        var flow = new Flow();
        var broken = Samples.Valid() with { Payer = null! };

        var result = await flow.IssueAsync(Flow.Request("pay_1", broken));

        Assert.Equal(WithholdingIssueOutcome.ValidationFailed, result.Outcome);
        Assert.Equal([new WithholdingValidationError(WithholdingValidationCode.Malformed, "snapshot")], result.Errors);
    }

    [Fact]
    public async Task Issue_ShouldGiveTheNumberBack_WhenTheCallerRollsBack()
    {
        var flow = new Flow();
        await using (var tx = new InMemoryWithholdingCertificateStore.FakeTransaction())
        {
            Assert.Equal(WithholdingIssueOutcome.Issued, (await flow.Service.IssueAsync(tx, Flow.Request("pay_1"))).Outcome);
            await tx.RollbackAsync();
        }

        Assert.Empty(flow.Store.Rows);
        var id = await flow.IssuedAsync("pay_1");
        Assert.Equal("WHT-2569-000001", flow.Row(id).DocumentNumber);
    }

    [Fact]
    public async Task Issue_ShouldStoreExactlyWhatItValidated_EvenIfTheCallersListChangesWhileTheIssueRuns()
    {
        // The number source runs between the first validation and the store. The caller's list is changed there, as another thread
        // could at any point; the service works on its own copy, so the certificate stored is the one that passed validation.
        var flow = new Flow();
        var valid = Samples.Valid();
        var rows = new List<WithholdingIncomeRow> { valid.Rows[0] };
        flow.Numbers.OnNext = () => rows[0] = valid.Rows[0] with { TaxWithheldSatang = 1 };

        var result = await flow.IssueAsync(Flow.Request("pay_1", valid with { Rows = rows }));

        Assert.Equal(WithholdingIssueOutcome.Issued, result.Outcome);
        var row = flow.Row(result.DocumentId!.Value);
        var stored = WithholdingSnapshotSerializer.Deserialize(row.SnapshotJson, row.SnapshotSha256);
        Assert.Empty(WithholdingSnapshotValidator.Validate(stored, Samples.Catalogue()));
        Assert.Equal(30_000, stored.Rows[0].TaxWithheldSatang);
    }

    [Fact]
    public async Task Issue_ShouldReplaceACertificate_CancellingTheOldOneAndNumberingTheNewOne()
    {
        var flow = new Flow();
        var firstId = await flow.IssuedAsync("pay_1");

        var result = await flow.IssueAsync(Flow.Request("pay_1", Replacing(firstId, "WHT-2569-000001"), revision: 2));

        Assert.Equal(WithholdingIssueOutcome.Issued, result.Outcome);
        var old = flow.Row(firstId);
        Assert.Equal(WithholdingCertificateState.Cancelled, old.State);
        Assert.Equal(result.DocumentId, old.ReplacedByCertificateId);
        var replacement = flow.Row(result.DocumentId!.Value);
        Assert.Equal("WHT-2569-000002", replacement.DocumentNumber);
        Assert.Equal(2, replacement.Revision);
        Assert.Equal("pay_1", replacement.SourceEventId);
    }

    [Fact]
    public async Task Issue_ShouldRefuseAReplacement_AndCancelNothing_WhenItDoesNotMatchTheReplacedCertificate()
    {
        var flow = new Flow();
        var firstId = await flow.IssuedAsync("pay_1");
        await flow.IssuedAsync("pay_2");
        var wrongRevision = Flow.Request("pay_1", Replacing(firstId, "WHT-2569-000001"), revision: 3);
        var wrongNumber = Flow.Request("pay_1", Replacing(firstId, "WHT-2569-999999"), revision: 2);
        var wrongDate = Flow.Request("pay_1", Replacing(firstId, "WHT-2569-000001", new DateOnly(2026, 1, 1)), revision: 2);
        var wrongSource = Flow.Request("pay_3", Replacing(firstId, "WHT-2569-000001"), revision: 2);
        var unknown = Flow.Request("pay_1", Replacing(Guid.NewGuid(), "WHT-2569-000001"), revision: 2);
        var otherPayer = Flow.Request("pay_1", Replacing(firstId, "WHT-2569-000001"), revision: 2, payer: "payer-2");

        foreach (var request in new[] { wrongRevision, wrongNumber, wrongDate, wrongSource, unknown, otherPayer })
            Assert.Equal(WithholdingIssueOutcome.ReplacementInvalid, (await flow.IssueAsync(request)).Outcome);

        Assert.Equal(WithholdingCertificateState.Issued, flow.Row(firstId).State);
        Assert.Equal(2, flow.Numbers.Calls);
        Assert.Equal(2, flow.Store.Rows.Count);
    }

    [Fact]
    public async Task Issue_ShouldRefuseReplacingACancelledCertificateTwice()
    {
        var flow = new Flow();
        var firstId = await flow.IssuedAsync("pay_1");
        await flow.IssueAsync(Flow.Request("pay_1", Replacing(firstId, "WHT-2569-000001"), revision: 2));

        var again = await flow.IssueAsync(Flow.Request("pay_1", Replacing(firstId, "WHT-2569-000001"), revision: 3));

        Assert.Equal(WithholdingIssueOutcome.ReplacementInvalid, again.Outcome);
    }

    [Fact]
    public async Task Issue_ShouldRequireAReplacementForARevisionAbove1_AndForbidOneOnRevision1()
    {
        var flow = new Flow();
        var firstId = await flow.IssuedAsync("pay_1");

        Assert.Equal(WithholdingIssueOutcome.ReplacementInvalid, (await flow.IssueAsync(Flow.Request("pay_1", revision: 2))).Outcome);
        Assert.Equal(
            WithholdingIssueOutcome.ReplacementInvalid,
            (await flow.IssueAsync(Flow.Request("pay_9", Replacing(firstId, "WHT-2569-000001")))).Outcome);
    }

    private static IssueWithholdingCertificateRequest InSeries(string source, string? series, WithholdingCertificateSnapshot? snapshot = null, int revision = 1) =>
        Flow.Request(source, snapshot, revision) with { Series = series };

    [Fact]
    public async Task Issue_ShouldNumberEachSeriesOnItsOwnRun_AndPrintTheSeriesAsTheBookNumber()
    {
        var numbers = new Flow.SeriesNumbers();
        var flow = new Flow(numbers: numbers);

        var cr1 = await flow.IssueAsync(InSeries("pay_1", "CR"));
        var sf1 = await flow.IssueAsync(InSeries("pay_2", "SF"));
        var cr2 = await flow.IssueAsync(InSeries("pay_3", "CR"));

        Assert.Equal("CR-001/69", flow.Row(cr1.DocumentId!.Value).DocumentNumber);
        Assert.Equal("SF-001/69", flow.Row(sf1.DocumentId!.Value).DocumentNumber);
        Assert.Equal("CR-002/69", flow.Row(cr2.DocumentId!.Value).DocumentNumber);
        var row = flow.Row(cr1.DocumentId!.Value);
        Assert.Equal("CR", WithholdingSnapshotSerializer.Deserialize(row.SnapshotJson, row.SnapshotSha256).BookNumber);
        Assert.Equal(["CR", "SF", "CR"], numbers.Requested);
    }

    [Fact]
    public async Task Issue_ShouldAcceptABookNumberThatEqualsTheSeries_AndKeepTheBookNumberOfAnUnseriesCertificate()
    {
        var flow = new Flow(numbers: new Flow.SeriesNumbers());

        var same = await flow.IssueAsync(InSeries("pay_1", "CR", Samples.Valid() with { BookNumber = "CR" }));
        var plain = await flow.IssueAsync(InSeries("pay_2", null, Samples.Valid() with { BookNumber = "12" }));

        var plainRow = flow.Row(plain.DocumentId!.Value);
        Assert.Equal(WithholdingIssueOutcome.Issued, same.Outcome);
        Assert.Equal("12", WithholdingSnapshotSerializer.Deserialize(plainRow.SnapshotJson, plainRow.SnapshotSha256).BookNumber);
        Assert.Equal("N-001/69", plainRow.DocumentNumber);
    }

    [Fact]
    public async Task Issue_ShouldRefuseABookNumberThatContradictsTheSeries_AndAMalformedSeries_WithoutConsumingANumber()
    {
        var numbers = new Flow.SeriesNumbers();
        var flow = new Flow(numbers: numbers);

        var clash = await flow.IssueAsync(InSeries("pay_1", "CR", Samples.Valid() with { BookNumber = "SF" }));
        Assert.Equal(WithholdingIssueOutcome.ValidationFailed, clash.Outcome);
        Assert.Equal([new WithholdingValidationError(WithholdingValidationCode.Malformed, "book_number")], clash.Errors);

        foreach (var bad in new[] { "", "C:R", "-CR", new string('x', 21), "ก" })
        {
            var result = await flow.IssueAsync(InSeries("pay_2", bad));
            Assert.Equal(WithholdingIssueOutcome.ValidationFailed, result.Outcome);
            Assert.Equal([new WithholdingValidationError(WithholdingValidationCode.Malformed, "series")], result.Errors);
        }

        Assert.Empty(numbers.Requested);
        Assert.Empty(flow.Store.Rows);
    }

    [Fact]
    public async Task Issue_ShouldGiveTheSeriesNumberBack_WhenTheCallerRollsBack()
    {
        var flow = new Flow(numbers: new Flow.SeriesNumbers());
        await using (var tx = new InMemoryWithholdingCertificateStore.FakeTransaction())
        {
            await flow.Service.IssueAsync(tx, InSeries("pay_1", "CR"));
            await tx.RollbackAsync();
        }

        var again = await flow.IssueAsync(InSeries("pay_1", "CR"));

        Assert.Equal("CR-001/69", flow.Row(again.DocumentId!.Value).DocumentNumber);
    }

    [Fact]
    public async Task Issue_ShouldFailLoudly_WhenTheNumberSourceDoesNotSupportSeries()
    {
        var flow = new Flow(); // CountingNumbers implements only the unseries method

        await Assert.ThrowsAsync<NotSupportedException>(() => flow.IssueAsync(InSeries("pay_1", "CR")));
        Assert.Equal(WithholdingIssueOutcome.Issued, (await flow.IssueAsync(InSeries("pay_2", null))).Outcome);
    }

    [Fact]
    public async Task Issue_ShouldSetTheIssueDateToTheCreationDateInBangkok_IgnoringTheCallers()
    {
        var flow = new Flow();

        var result = await flow.IssueAsync(Flow.Request("pay_1", Samples.Valid() with { IssueDate = new DateOnly(2020, 1, 1) }));

        var row = flow.Row(result.DocumentId!.Value);
        Assert.Equal(WithholdingIssueOutcome.Issued, result.Outcome);
        Assert.Equal(new DateOnly(2026, 10, 15), row.IssueDate);
        Assert.Equal(row.IssueDate, WithholdingSnapshotSerializer.Deserialize(row.SnapshotJson, row.SnapshotSha256).IssueDate);
    }

    [Theory]
    [InlineData("2026-10-14T16:59:59Z", "2026-10-14")] // 23:59:59 in Bangkok
    [InlineData("2026-10-14T17:00:00Z", "2026-10-15")] // midnight in Bangkok
    [InlineData("2026-12-31T17:00:00Z", "2027-01-01")] // the turn of the year
    public async Task Issue_ShouldUseTheBangkokDate_NotTheUtcDate(string utcNow, string expected)
    {
        var flow = new Flow(now: DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture));

        // A payment made today (Bangkok) must not be rejected as "after the issue date".
        var s = Samples.Valid();
        var date = DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        var result = await flow.IssueAsync(Flow.Request("pay_1", s with { Rows = [s.Rows[0] with { DatePaid = date }] }));

        Assert.Equal(WithholdingIssueOutcome.Issued, result.Outcome);
        Assert.Equal(date, flow.Row(result.DocumentId!.Value).IssueDate);
    }

    [Fact]
    public async Task Issue_ShouldRefuseADatePaidAfterToday_BecauseTheIssueDateIsTheClocks()
    {
        var flow = new Flow(); // today in Bangkok: 2026-10-15
        var s = Samples.Valid();

        var result = await flow.IssueAsync(Flow.Request("pay_1", s with { Rows = [s.Rows[0] with { DatePaid = new DateOnly(2026, 10, 16) }] }));

        Assert.Equal(WithholdingIssueOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "rows[0].date_paid"), result.Errors);
    }

    [Fact]
    public async Task Issue_ShouldRecordTheSignatureHash_OutsideTheSnapshot_AndNothingWhenThereIsNone()
    {
        var flow = new Flow();
        var png = new WithholdingSignatureImage(SignatureFixtures.Png);

        var signed = await flow.IssueAsync(Flow.Request("pay_1") with { Signature = png });
        var plain = await flow.IssueAsync(Flow.Request("pay_2"));

        var row = flow.Row(signed.DocumentId!.Value);
        Assert.Equal(png.Sha256, row.SignatureSha256);
        Assert.Equal(64, row.SignatureSha256!.Length);
        Assert.DoesNotContain(png.Sha256, row.SnapshotJson, StringComparison.Ordinal);
        Assert.Null(flow.Row(plain.DocumentId!.Value).SignatureSha256);
    }

    [Fact]
    public async Task Issue_ShouldRejectBadArguments()
    {
        var flow = new Flow();
        await using var tx = new InMemoryWithholdingCertificateStore.FakeTransaction();

        await Assert.ThrowsAsync<ArgumentNullException>(() => flow.Service.IssueAsync(null!, Flow.Request("a")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => flow.Service.IssueAsync(tx, null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => flow.Service.IssueAsync(tx, Flow.Request("a", payer: " ")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => flow.Service.IssueAsync(tx, Flow.Request("a") with { Key = default }));
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        var f = new Flow();
        var logger = new CapturingLogger<WithholdingCertificateService>();
        var v = new WithholdingValidationOptions();
        Assert.Throws<ArgumentNullException>(() => new WithholdingCertificateService(null!, f.Numbers, Samples.Catalogue(), v, f.Clock, logger));
        Assert.Throws<ArgumentNullException>(() => new WithholdingCertificateService(f.Store, null!, Samples.Catalogue(), v, f.Clock, logger));
        Assert.Throws<ArgumentNullException>(() => new WithholdingCertificateService(f.Store, f.Numbers, null!, v, f.Clock, logger));
        Assert.Throws<ArgumentNullException>(() => new WithholdingCertificateService(f.Store, f.Numbers, Samples.Catalogue(), null!, f.Clock, logger));
        Assert.Throws<ArgumentNullException>(() => new WithholdingCertificateService(f.Store, f.Numbers, Samples.Catalogue(), v, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new WithholdingCertificateService(f.Store, f.Numbers, Samples.Catalogue(), v, f.Clock, null!));
    }

    [Fact]
    public void Key_ShouldRejectABlankSourceAndARevisionBelow1()
    {
        Assert.Throws<ArgumentException>(() => new WithholdingCertificateKey(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WithholdingCertificateKey("a", 0));
        Assert.Equal(1, new WithholdingCertificateKey("a").Revision);
    }
}
