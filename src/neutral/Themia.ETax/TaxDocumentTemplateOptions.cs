namespace Themia.ETax;

/// <summary>
/// The adopter's template choice. It does not change a document that is already issued: each stores the template version it was issued under
/// and is always rendered with it.
/// </summary>
public sealed class TaxDocumentTemplateOptions
{
    /// <summary>
    /// The template version NEW documents are issued under. Default <c>"1"</c>, so upgrading changes nothing until the adopter opts in with
    /// <c>"2"</c> (template 1 plus the amount in Thai words under the total, units digit 1 read "เอ็ด") or <c>"3"</c> (the same, <c>TensOnly</c>: หนึ่งร้อยหนึ่ง but ยี่สิบเอ็ด), or <c>"4"</c> / <c>"5"</c> (the receipt/tax invoice and credit note on A4 portrait / A5 landscape). See <see cref="TaxDocumentTemplates.IsSupported"/>.
    /// </summary>
    public string TemplateVersion { get; set; } = TaxDocumentTemplates.CurrentVersion;

    /// <summary>
    /// The seller's logo for the paged templates ("4" and "5"), the same for every seller. Documents issued with it record its hash and print it again when
    /// re-rendered; after you swap it, those documents are reported unavailable unless you register an <see cref="ITaxDocumentLogoSource"/> that keeps the old image.
    /// Null prints no logo box. Setting it with another template version throws.
    /// </summary>
    public TaxDocumentLogo? Logo { get; set; }

    internal void Validate()
    {
        if (Logo is not null && TaxDocumentTemplates.PageSizeOf(TemplateVersion) is null)
            throw new InvalidOperationException($"{nameof(TaxDocumentTemplateOptions)}.{nameof(Logo)} needs {nameof(TemplateVersion)} \"4\" or \"5\"; '{TemplateVersion}' has no logo slot.");
        if (!TaxDocumentTemplates.IsSupported(TemplateVersion))
            throw new InvalidOperationException($"{nameof(TaxDocumentTemplateOptions)}.{nameof(TemplateVersion)} '{TemplateVersion}' is not a supported template version.");
    }
}
