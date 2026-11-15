using System.Globalization;
using System.Text;

namespace Themia.Conversations.Internal;

/// <summary>The name an attachment is stored and served under. The client's name is untrusted: it never reaches a path or a header unchanged.</summary>
internal static class AttachmentFileName
{
    internal const string Fallback = "attachment";
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '|', '?', '*', '/', '\\'];

    /// <summary>
    /// The stored name: the leaf of the client's name without control, format (U+202E and friends) or path characters, and with the extension forced to the
    /// one the stored <paramref name="contentType"/> implies. A client that sends <c>invoice.hta</c> for a PDF gets <c>invoice.pdf</c>, so a download can never
    /// carry an executable or renderable extension the allow-list refused.
    /// </summary>
    public static string Sanitize(string? name, string contentType)
    {
        var tail = (name ?? string.Empty).Replace('\\', '/').Split('/')[^1];
        var cleaned = new StringBuilder(tail.Length);
        foreach (var c in tail)
        {
            if (!IsDisallowed(c))
            {
                cleaned.Append(c);
            }
        }

        var stem = cleaned.ToString().Trim().TrimStart('.');
        var extension = ExtensionOf(contentType);
        if (extension is null)
        {
            return Cap(stem, ConversationAttachmentUploadsMigrationWidth);
        }

        var dot = stem.LastIndexOf('.');
        if (dot > 0)
        {
            stem = stem[..dot];
        }

        stem = stem.TrimEnd().TrimEnd('.');
        return Cap(stem, ConversationAttachmentUploadsMigrationWidth - extension.Length) + extension;
    }

    private static bool IsDisallowed(char c) =>
        char.IsControl(c)
        || Array.IndexOf(Forbidden, c) >= 0
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static string? ExtensionOf(string contentType) => contentType switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => null,
    };

    private static string Cap(string value, int width)
    {
        if (value.Length > width)
        {
            value = value[..width];
            if (value.Length > 0 && char.IsHighSurrogate(value[^1]))
            {
                value = value[..^1];
            }
        }

        return value.Length == 0 ? Fallback : value;
    }

    /// <summary>An attachment disposition with an ASCII fallback and the RFC 5987 name for clients that read it.</summary>
    public static string ContentDisposition(string fileName)
    {
        var ascii = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
        {
            ascii.Append(c is >= ' ' and < (char)127 && c is not '"' and not '\\' and not '%' ? c : '_');
        }

        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private const int ConversationAttachmentUploadsMigrationWidth = Migrations.ConversationAttachmentUploadsMigration.FileNameWidth;
}
