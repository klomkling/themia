using Xunit;

namespace Themia.ETax.Tests;

public sealed class LogoSourceTests
{
    private static readonly TaxDocumentLogo Brand = Png(1);
    private static readonly TaxDocumentLogo Rebrand = Png(2);

    private static TaxDocumentLogo Png(byte tag) => new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, tag]);

    private static TaxDocumentTemplateOptions Paged(TaxDocumentLogo? logo, string version = "4") => new() { TemplateVersion = version, Logo = logo };

    private static async Task<TaxDocumentRecord> IssueAsync(Flow flow, string source = "pay_1", string seller = Flow.Seller)
    {
        var result = await flow.IssueAsync(Flow.Request(source, seller: seller));
        Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
        return (await flow.Store.GetAsync(null, result.DocumentId!.Value, seller))!;
    }

    [Fact]
    public void Sha256_ShouldBeTheLowercaseHexHashOfTheImageBytes()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1 };

        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), new TaxDocumentLogo(bytes).Sha256);
    }

    [Fact]
    public async Task IssueAsync_ShouldRecordTheLogoHash_WhenThePagedTemplateHasALogo()
    {
        var doc = await IssueAsync(new Flow(templates: Paged(Brand)));

        Assert.Contains($"\"logo_sha256\":\"{Brand.Sha256}\"", doc.SnapshotJson);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("5")]
    public async Task IssueAsync_ShouldOmitTheHash_WhenThereIsNoLogo(string version)
    {
        var doc = await IssueAsync(new Flow(templates: Paged(null, version)));

        Assert.DoesNotContain("logo_sha256", doc.SnapshotJson);
    }

    [Fact]
    public async Task IssueAsync_ShouldNotAskTheSource_WhenTheTemplateHasNoLogoSlot()
    {
        var doc = await IssueAsync(new Flow(templates: new TaxDocumentTemplateOptions(), logos: new FixedLogos(Brand)));

        Assert.DoesNotContain("logo_sha256", doc.SnapshotJson);
    }

    [Fact]
    public async Task IssueAsync_ShouldRefuseACallerSuppliedHash()
    {
        var flow = new Flow(templates: Paged(Brand));
        var request = Flow.Request(snapshot: Samples.Receipt() with { LogoSha256 = Brand.Sha256 });

        var result = await flow.IssueAsync(request);

        Assert.Equal(TaxDocumentIssueOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(result.Errors, e => e.Field == "logo_sha256");
    }

    [Fact]
    public async Task IssueAsync_ShouldTakeNoNumber_WhenTheLogoSourceFails()
    {
        var flow = new Flow(templates: Paged(null), logos: new ThrowingLogos());

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.IssueAsync(Flow.Request()));

        Assert.Equal(0, flow.Numbers.Calls);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void Validate_ShouldRejectAMalformedHash(string sha)
    {
        var errors = TaxDocumentSnapshotValidator.Validate(Samples.Receipt() with { LogoSha256 = sha }, new TaxDocumentValidationOptions());

        Assert.Contains(errors, e => e.Field == "logo_sha256");
    }

    [Fact]
    public async Task RenderCopyAsync_ShouldPrintTheLogoTheDocumentWasIssuedWith()
    {
        var flow = new Flow(templates: Paged(Brand));
        var doc = await IssueAsync(flow);

        await flow.Service.RenderCopyAsync(doc.Id, Flow.Seller);

        Assert.Contains(Convert.ToBase64String(PngBytes(1)), flow.Pdf.Htmls[^1]);
    }

    [Fact]
    public async Task RenderCopyAsync_ShouldThrow_WhenTheHostSwappedTheLogoAndKeptNoOldOne()
    {
        var templates = Paged(Brand);
        var flow = new Flow(templates: templates);
        var doc = await IssueAsync(flow);
        templates.Logo = Rebrand;

        var ex = await Assert.ThrowsAsync<TaxDocumentLogoException>(() => flow.Service.RenderCopyAsync(doc.Id, Flow.Seller));

        Assert.Equal(Brand.Sha256, ex.LogoSha256);
        Assert.DoesNotContain(Brand.Sha256, ex.Message);
    }

    [Fact]
    public async Task RenderCopyAsync_ShouldPrintNoLogo_ForADocumentIssuedWithoutOne_EvenIfTheHostHasOneNow()
    {
        var templates = Paged(null);
        var flow = new Flow(templates: templates);
        var doc = await IssueAsync(flow);
        templates.Logo = Brand;

        await flow.Service.RenderCopyAsync(doc.Id, Flow.Seller);

        Assert.DoesNotContain("class=\"logo\"", flow.Pdf.Htmls[^1]);
    }

    [Fact]
    public async Task RenderCopyAsync_ShouldFindAnOlderLogoBySellerAndHash_InASourceThatKeepsThem()
    {
        var logos = new PerSellerLogos();
        logos.Current["seller-1"] = Brand;
        logos.Current["seller-2"] = Rebrand;
        var flow = new Flow(templates: Paged(null), logos: logos);
        var first = await IssueAsync(flow, "pay_1", "seller-1");
        var second = await IssueAsync(flow, "pay_2", "seller-2");
        logos.Held.Add(("seller-1", Brand));
        logos.Held.Add(("seller-2", Rebrand));
        logos.Current["seller-1"] = Rebrand;

        await flow.Service.RenderCopyAsync(first.Id, "seller-1");
        await flow.Service.RenderCopyAsync(second.Id, "seller-2");

        Assert.Contains(Convert.ToBase64String(PngBytes(1)), flow.Pdf.Htmls[^2]);
        Assert.Contains(Convert.ToBase64String(PngBytes(2)), flow.Pdf.Htmls[^1]);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldFailTheDocumentAtOnceWithoutRetry_WhenItsLogoIsGone()
    {
        var templates = Paged(Brand);
        var flow = new Flow(templates: templates);
        var doc = await IssueAsync(flow);
        templates.Logo = Rebrand;

        await flow.Sender.RunOnceAsync();

        var row = await flow.GetAsync(doc.Id);
        Assert.Equal(TaxDocumentState.Failed, row.State);
        Assert.Equal("logo_unavailable", row.LastErrorCode);
        Assert.Empty(flow.Mailer.Sent);
    }

    [Fact]
    public async Task RenderWithLogoAsync_ShouldRefuseALogoOtherThanTheRecordedOne()
    {
        var flow = new Flow(templates: Paged(Brand));
        var doc = await IssueAsync(flow);
        var snapshot = TaxDocumentSnapshotSerializer.Deserialize(doc.SnapshotJson, doc.SnapshotSha256);

        await Assert.ThrowsAsync<TaxDocumentLogoException>(() => flow.Renderer.RenderWithLogoAsync(snapshot, "4", TaxDocumentMarker.Copy, Rebrand));
        await Assert.ThrowsAsync<TaxDocumentLogoException>(() => flow.Renderer.RenderWithLogoAsync(snapshot, "4", TaxDocumentMarker.Copy, null));
        await Assert.ThrowsAsync<TaxDocumentLogoException>(() => flow.Renderer.RenderAsync(snapshot, "4", TaxDocumentMarker.Copy));
        await flow.Renderer.RenderWithLogoAsync(snapshot, "4", TaxDocumentMarker.Copy, Brand);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    public void Validate_ShouldThrow_WhenALogoIsSetWithoutALogoSlot(string version) =>
        Assert.Throws<InvalidOperationException>(() => Paged(Brand, version).Validate());

    private static byte[] PngBytes(byte tag) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, tag];

    private sealed class ThrowingLogos : ITaxDocumentLogoSource
    {
        public Task<TaxDocumentLogo?> GetCurrentAsync(string sellerId, CancellationToken ct = default) => throw new InvalidOperationException("logo store down");

        public Task<TaxDocumentLogo?> FindAsync(string sellerId, string sha256, CancellationToken ct = default) => throw new InvalidOperationException("logo store down");
    }

    private sealed class FixedLogos(TaxDocumentLogo logo) : ITaxDocumentLogoSource
    {
        public Task<TaxDocumentLogo?> GetCurrentAsync(string sellerId, CancellationToken ct = default) => Task.FromResult<TaxDocumentLogo?>(logo);

        public Task<TaxDocumentLogo?> FindAsync(string sellerId, string sha256, CancellationToken ct = default) =>
            Task.FromResult<TaxDocumentLogo?>(logo.Sha256 == sha256 ? logo : null);
    }

    private sealed class PerSellerLogos : ITaxDocumentLogoSource
    {
        public Dictionary<string, TaxDocumentLogo> Current { get; } = [];
        public List<(string Seller, TaxDocumentLogo Logo)> Held { get; } = [];

        public Task<TaxDocumentLogo?> GetCurrentAsync(string sellerId, CancellationToken ct = default) =>
            Task.FromResult(Current.GetValueOrDefault(sellerId));

        public Task<TaxDocumentLogo?> FindAsync(string sellerId, string sha256, CancellationToken ct = default) =>
            Task.FromResult<TaxDocumentLogo?>(Held.FirstOrDefault(h => h.Seller == sellerId && h.Logo.Sha256 == sha256).Logo);
    }
}
