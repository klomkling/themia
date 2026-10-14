namespace Themia.ETax;

/// <summary>A rendered, normalised PDF and what identifies the renderer that produced it.</summary>
/// <param name="Pdf">The normalised PDF bytes.</param>
/// <param name="PdfSha256">Lower-case hex SHA-256 of <paramref name="Pdf"/>.</param>
/// <param name="RendererVersion"><c>&lt;browser version&gt;|n&lt;normaliser version&gt;</c>. The normaliser is part of it
/// because changing how dates are patched changes the hash.</param>
public sealed record TaxDocumentRender(byte[] Pdf, string PdfSha256, string RendererVersion);
