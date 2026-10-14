namespace Themia.Pdf;

/// <summary>A rendered PDF and the browser build that printed it.</summary>
/// <param name="Bytes">The PDF.</param>
/// <param name="RendererVersion">Version of the browser instance that printed <paramref name="Bytes"/>
/// (e.g. <c>HeadlessChrome/131.0.6778.204</c>). Read from the same browser as the print, so a relaunch between
/// the two cannot mislabel it.</param>
public sealed record PdfRenderResult(byte[] Bytes, string RendererVersion);
