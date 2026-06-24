namespace Themia.Export;

/// <summary>A produced export file. The host streams it, e.g.
/// <c>Results.File(r.Content, r.ContentType, r.FileName)</c>; this type carries no ASP.NET dependency.</summary>
/// <param name="Content">The file bytes. Callers should treat this array as read-only; it is the finished output.</param>
/// <param name="ContentType">The MIME content type.</param>
/// <param name="FileName">The suggested download file name.</param>
public sealed record ExportResult(byte[] Content, string ContentType, string FileName);
