using System.Security.Cryptography;
using HandlebarsDotNet;
using Themia.Pdf;

namespace Themia.ETax.Withholding;

/// <summary>
/// Renders a stored withholding certificate as a PDF on the layout of the official form (หนังสือรับรองการหักภาษี ณ ที่จ่าย, form 50 ทวิ),
/// with the template version it was issued under. The same inputs give the same bytes: embedded Sarabun, no clock, no culture, PDF dates
/// pinned to the issue date. Rendering never validates and never logs; a snapshot is checked against its hash before it is printed.
/// </summary>
public sealed class WithholdingCertificateRenderer
{
    private static readonly Lazy<string> FontCss = new(TaxDocumentRenderModel.FontFaceCss);

    internal static readonly PdfRenderOptions PrintOptions = new()
    {
        PaperFormat = PdfPaperFormat.A4,
        PrintBackground = true,
        // A tagged PDF embeds structure-element ids from a per-browser counter, so two renders would never match.
        Tagged = false,
        MarginTop = "8mm",
        MarginBottom = "8mm",
        MarginLeft = "8mm",
        MarginRight = "8mm",
    };

    // Deliberately NOT the shared IHtmlTemplateRenderer: that one runs the adopter's ThemiaPdfOptions.ConfigureHandlebars (extra helpers,
    // overridden built-ins, changed escaping), and any of those would silently change what a pinned template version prints.
    private static readonly IHandlebars Handlebars = HandlebarsDotNet.Handlebars.Create();

    private readonly IVersionedPdfRenderer _pdf;

    /// <summary>Creates the renderer.</summary>
    /// <param name="pdf">The PDF renderer (Themia.Pdf's Puppeteer renderer when registered with <c>AddThemiaETaxWithholdingRendering</c>).</param>
    public WithholdingCertificateRenderer(IVersionedPdfRenderer pdf)
    {
        _pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
    }

    /// <summary>Renders a snapshot with <paramref name="templateVersion"/>.</summary>
    /// <param name="snapshot">The certificate content.</param>
    /// <param name="templateVersion">The template version stored with the certificate (see <see cref="WithholdingTemplates.IsSupported"/>).</param>
    /// <param name="copy">Which of the two listed copies this is.</param>
    /// <param name="mode">As issued, a replacement copy (ใบแทน) or cancelled.</param>
    /// <param name="signature">The payer's signature image to print on the signature line, or <see langword="null"/> for none. Render-time only: it is not part of the stored certificate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">The template version is unknown.</exception>
    public async Task<TaxDocumentRender> RenderAsync(
        WithholdingCertificateSnapshot snapshot, string templateVersion, WithholdingCopy copy, WithholdingRenderMode mode,
        WithholdingSignatureImage? signature = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(templateVersion);

        var html = RenderHtml(snapshot, templateVersion, copy, mode, signature);
        var printed = await _pdf.RenderHtmlWithVersionAsync(html, PrintOptions, ct).ConfigureAwait(false);
        var pdf = TaxDocumentPdfNormaliser.NormaliseDates(printed.Bytes, snapshot.IssueDate);
        return new TaxDocumentRender(
            pdf,
            Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant(),
            $"{printed.RendererVersion}|{TaxDocumentPdfNormaliser.Version}");
    }

    /// <summary>
    /// Renders a stored certificate: verifies the snapshot against its hash, checks the signature image against the SHA-256 recorded when the
    /// certificate was issued (<see cref="WithholdingCertificateRecord.SignatureSha256"/>), renders it with the template version it was issued under, and
    /// prints a cancelled certificate with the cancelled watermark whatever <paramref name="mode"/> asks.
    /// </summary>
    /// <param name="record">The stored certificate.</param>
    /// <param name="copy">Which of the two listed copies this is.</param>
    /// <param name="mode">As issued or a replacement copy (ใบแทน); ignored for a cancelled certificate.</param>
    /// <param name="signature">The image recorded at issue, to print on the signature line; <see langword="null"/> exactly when the certificate was issued without one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="WithholdingSignatureException">The image differs from the recorded one, is missing, or was not recorded at issue.</exception>
    /// <exception cref="TaxDocumentSnapshotException">The stored snapshot does not match its hash or cannot be read.</exception>
    /// <exception cref="ArgumentException">The stored template version is unknown.</exception>
    public Task<TaxDocumentRender> RenderStoredAsync(
        WithholdingCertificateRecord record, WithholdingCopy copy, WithholdingRenderMode mode = WithholdingRenderMode.Issued,
        WithholdingSignatureImage? signature = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        CheckSignature(record, signature);
        var snapshot = WithholdingSnapshotSerializer.Deserialize(record.SnapshotJson, record.SnapshotSha256);
        var effective = record.State == WithholdingCertificateState.Cancelled ? WithholdingRenderMode.Cancelled : mode;
        return RenderAsync(snapshot, record.TemplateVersion, copy, effective, signature, ct);
    }

    // An old certificate is never re-rendered with a different signer's signature, with none, or with one it never had.
    private static void CheckSignature(WithholdingCertificateRecord record, WithholdingSignatureImage? signature)
    {
        if (record.SignatureSha256 is null)
        {
            if (signature is not null)
                throw new WithholdingSignatureException(WithholdingSignatureFailure.NotIssuedWithOne);
        }
        else if (signature is null)
        {
            throw new WithholdingSignatureException(WithholdingSignatureFailure.MissingImage);
        }
        else if (!string.Equals(signature.Sha256, record.SignatureSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new WithholdingSignatureException(WithholdingSignatureFailure.DifferentImage);
        }
    }

    internal string RenderHtml(
        WithholdingCertificateSnapshot snapshot, string templateVersion, WithholdingCopy copy, WithholdingRenderMode mode, WithholdingSignatureImage? signature = null)
    {
        if (!Enum.IsDefined(copy))
            throw new ArgumentOutOfRangeException(nameof(copy), copy, "Unknown copy.");
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown render mode.");

        return Handlebars.Compile(WithholdingTemplates.Get(templateVersion))(
            WithholdingRenderModel.Build(snapshot, copy, mode, FontCss.Value, signature));
    }
}
