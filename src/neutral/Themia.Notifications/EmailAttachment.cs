using System.Globalization;
using System.Net.Mime;
using System.Text;

namespace Themia.Notifications;

/// <summary>
/// A file attached to an email (<see cref="NotificationMessage.Attachments"/>), for example a rendered PDF.
/// </summary>
/// <remarks>
/// Checked on construction, for the reasons given on <see cref="NotificationMessage.Headers"/>: the file name ends up in a
/// <c>Content-Disposition</c> header, so a line break in it injects headers, and a path in it is shown to the recipient as the
/// name they save the file under. Which content types are allowed and how large the attachments may be together are decisions of
/// the sender (<c>SmtpEmailOptions</c>), not of this type. The content is not copied: do not change the array after handing it over.
/// </remarks>
public sealed record EmailAttachment
{
    /// <summary>The longest file name accepted, in characters (common mail clients and file systems stop at 255).</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>Creates the attachment.</summary>
    /// <param name="fileName">The name shown to the recipient: not blank, at most <see cref="MaxFileNameLength"/> characters, no path separator, no control character.</param>
    /// <param name="contentType">The media type, <c>type/subtype</c> without parameters, for example <c>application/pdf</c>.</param>
    /// <param name="content">The file bytes; not empty.</param>
    /// <exception cref="ArgumentException">A value breaks the rules above.</exception>
    public EmailAttachment(string fileName, string contentType, ReadOnlyMemory<byte> content)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > MaxFileNameLength
            || !IsSafeFileName(fileName)
            || fileName is "." or "..")
        {
            throw new ArgumentException(
                $"The attachment file name must be non-blank, at most {MaxFileNameLength} characters, and without path separators, quotes or control characters.",
                nameof(fileName));
        }

        if (!TryNormaliseContentType(contentType, out var normalised))
        {
            throw new ArgumentException(
                "The attachment content type must be a plain media type such as 'application/pdf' (no parameters, no whitespace).",
                nameof(contentType));
        }

        if (content.IsEmpty)
            throw new ArgumentException("The attachment is empty.", nameof(content));

        FileName = fileName;
        ContentType = normalised;
        Content = content;
    }

    /// <summary>The name shown to the recipient.</summary>
    public string FileName { get; }

    /// <summary>The media type, lower-cased (<c>application/pdf</c>).</summary>
    public string ContentType { get; }

    /// <summary>The file bytes.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    // The generated ToString would list Content; keep the bytes out of any log line that prints an attachment.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("FileName = ").Append(FileName).Append(", ContentType = ").Append(ContentType)
            .Append(", Length = ").Append(Content.Length);
        return true;
    }

    // Beyond the header-injection characters: the name is what the recipient reads, so characters that make it display as something it
    // is not are refused too. Format covers the bidi overrides and isolates (U+202A-202E, U+2066-2069) and zero-width characters;
    // an unpaired surrogate is not text at all. Scripts' own marks (Thai vowels, tone marks) are letters/marks, not Format.
    private static bool IsSafeFileName(string name)
    {
        foreach (var rune in EnumerateRunes(name))
        {
            if (rune is null)
                return false;

            var r = rune.Value;
            if (Rune.IsControl(r) || r.Value is '/' or '\\' or '"')
                return false;

            if (Rune.GetUnicodeCategory(r) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                return false;
            }
        }

        return true;
    }

    // Null marks an invalid (unpaired surrogate) position.
    private static IEnumerable<Rune?> EnumerateRunes(string name)
    {
        var i = 0;
        while (i < name.Length)
        {
            if (Rune.TryGetRuneAt(name, i, out var rune))
            {
                i += rune.Utf16SequenceLength;
                yield return rune;
            }
            else
            {
                i++;
                yield return null;
            }
        }
    }

    private static bool TryNormaliseContentType(string? value, out string normalised)
    {
        normalised = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Any(c => c is <= ' ' or ';' or >= (char)127))
            return false;

        try
        {
            var parsed = new ContentType(value);
            normalised = parsed.MediaType.ToLowerInvariant();
            return parsed.MediaType.Count(c => c == '/') == 1 && !parsed.MediaType.StartsWith('/') && !parsed.MediaType.EndsWith('/');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
