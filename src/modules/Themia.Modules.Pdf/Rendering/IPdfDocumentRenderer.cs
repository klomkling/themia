using Themia.Pdf;

namespace Themia.Modules.Pdf.Rendering;

/// <summary>Renders a stored template by key: resolves (tenant → global), merges the model, prints a PDF.</summary>
public interface IPdfDocumentRenderer
{
    /// <summary>Resolves the template for <paramref name="key"/>, merges it with <paramref name="model"/>,
    /// and prints the result to a PDF. Throws <see cref="TemplateNotFoundException"/> if unresolved.</summary>
    Task<byte[]> RenderAsync(string key, object model, PdfRenderOptions? options = null, CancellationToken cancellationToken = default);
}
