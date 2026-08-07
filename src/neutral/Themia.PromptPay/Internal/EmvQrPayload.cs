using System.Text;

namespace Themia.PromptPay.Internal;

/// <summary>
/// EMVCo TLV assembly and the CRC-16 checksum tag. Every payload this package emits is built here.
/// </summary>
/// <remarks>
/// The format is <c>ID(2) LENGTH(2) VALUE</c>, repeated. The length field being exactly two decimal
/// digits is the source of every size limit in this package: no single tag's value can exceed 99
/// characters, and a template tag's value is the concatenation of its sub-tags, so the template's own
/// 99 bounds all of them together. See <see cref="BillerRegistration.MaxReferenceLength"/>.
/// </remarks>
internal static class EmvQrPayload
{
    /// <summary>The largest value a single tag can carry, set by the two-digit length field.</summary>
    internal const int MaxTagValueLength = 99;

    /// <summary>The tag carrying the CRC checksum. Must be last in the payload.</summary>
    internal const string ChecksumTagId = "63";

    /// <summary>Renders one tag. Throws rather than emitting a payload no reader can parse.</summary>
    internal static string Tag(string id, string value)
    {
        if (value.Length > MaxTagValueLength)
        {
            throw new ArgumentException(
                $"EMVCo tag '{id}' value is {value.Length} characters; the two-digit length field caps it at "
                + $"{MaxTagValueLength}. A longer value would encode a length that wraps and produce a payload "
                + "that decodes as different data rather than failing.",
                nameof(value));
        }

        return string.Concat(id, value.Length.ToString("00", System.Globalization.CultureInfo.InvariantCulture), value);
    }

    /// <summary>Concatenates rendered tags into a template's value or the root payload.</summary>
    internal static string Concat(params string[] tags)
    {
        var builder = new StringBuilder();
        foreach (var tag in tags)
        {
            builder.Append(tag);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends the checksum tag and its value, completing the payload.
    /// </summary>
    /// <remarks>
    /// The checksum covers the payload <b>including</b> this tag's own id and length (<c>"6304"</c>) —
    /// omitting them yields a payload every bank app rejects, and is the single most common way to get
    /// this wrong. The golden vectors in the test suite pin it.
    /// </remarks>
    internal static string WithChecksum(string payload)
    {
        var withTagHeader = payload + ChecksumTagId + "04";
        return withTagHeader + Crc16.Compute(withTagHeader);
    }
}
