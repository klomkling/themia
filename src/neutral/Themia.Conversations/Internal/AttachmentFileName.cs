using System.Text;

namespace Themia.Conversations.Internal;

/// <summary>The name an attachment is stored and served under. The client's name is untrusted: it never reaches a path or a header unchanged.</summary>
internal static class AttachmentFileName
{
    private const string Fallback = "attachment";
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '|', '?', '*', '/', '\\'];

    public static string Sanitize(string? name)
    {
        var tail = (name ?? string.Empty).Replace('\\', '/').Split('/')[^1];
        var cleaned = new StringBuilder(tail.Length);
        foreach (var c in tail)
        {
            if (!char.IsControl(c) && Array.IndexOf(Forbidden, c) < 0)
            {
                cleaned.Append(c);
            }
        }

        var result = cleaned.ToString().Trim().TrimStart('.');
        if (result.Length > ConversationAttachmentUploadsMigrationWidth)
        {
            result = result[..ConversationAttachmentUploadsMigrationWidth];
            if (char.IsHighSurrogate(result[^1]))
            {
                result = result[..^1];
            }
        }

        return result.Length == 0 ? Fallback : result;
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
