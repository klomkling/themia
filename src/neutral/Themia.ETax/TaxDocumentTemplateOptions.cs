namespace Themia.ETax;

/// <summary>
/// The adopter's template choice. It does not change a document that is already issued: each stores the template version it was issued under
/// and is always rendered with it.
/// </summary>
public sealed class TaxDocumentTemplateOptions
{
    /// <summary>
    /// The template version NEW documents are issued under. Default <c>"1"</c>, so upgrading changes nothing until the adopter opts in with
    /// <c>"2"</c> (template 1 plus the amount in Thai words under the total, units digit 1 read "เอ็ด") or <c>"3"</c> (the same, <c>TensOnly</c>: หนึ่งร้อยหนึ่ง but ยี่สิบเอ็ด). See <see cref="TaxDocumentTemplates.IsSupported"/>.
    /// </summary>
    public string TemplateVersion { get; set; } = TaxDocumentTemplates.CurrentVersion;

    internal void Validate()
    {
        if (!TaxDocumentTemplates.IsSupported(TemplateVersion))
            throw new InvalidOperationException($"{nameof(TaxDocumentTemplateOptions)}.{nameof(TemplateVersion)} '{TemplateVersion}' is not a supported template version.");
    }
}
