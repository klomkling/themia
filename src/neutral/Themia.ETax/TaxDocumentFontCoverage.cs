using System.Buffers.Binary;
using System.Text;

namespace Themia.ETax;

/// <summary>
/// Which characters the embedded Sarabun faces can print. A character outside them would fall back to a font
/// of the host's choosing, which differs between machines and would break byte-identical re-rendering, so Issue
/// refuses such text (see <see cref="TaxDocumentValidationCode.UnsupportedCharacter"/>).
/// </summary>
/// <remarks>
/// Derived from the cmap of the embedded fonts themselves, never from a hand-written range table, so it cannot
/// drift from what is actually printed. A character is covered only when both the regular and the bold face have it.
/// </remarks>
public static class TaxDocumentFontCoverage
{
    private static readonly Lazy<HashSet<int>> Covered = new(() =>
    {
        var set = ReadCodePoints(TaxDocumentFonts.Regular);
        set.IntersectWith(ReadCodePoints(TaxDocumentFonts.Bold));
        return set;
    });

    /// <summary>True when every character of <paramref name="text"/> prints in the embedded faces. Line breaks and zero-width default-ignorables are allowed.</summary>
    /// <param name="text">The text to check; <see langword="null"/> is covered.</param>
    public static bool Covers(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return true;

        var covered = Covered.Value;
        for (var i = 0; i < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, i, out var rune))
                return false;
            if (!IsAllowedWithoutGlyph(rune.Value) && !covered.Contains(rune.Value))
                return false;
            i += rune.Utf16SequenceLength;
        }

        return true;
    }

    // Line breaks, and the default-ignorable code points (zero-width space/non-joiner/joiner, word joiner, BOM):
    // Chromium draws nothing for them and Thai text pasted from the web carries them, so they need no glyph.
    private static bool IsAllowedWithoutGlyph(int codePoint) =>
        codePoint is '\n' or '\r' or 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF;

    // Reads the Unicode cmap (format 4 and 12) of a TrueType font.
    internal static HashSet<int> ReadCodePoints(byte[] font)
    {
        var tables = ReadTableDirectory(font);
        if (!tables.TryGetValue("cmap", out var cmap))
            throw new InvalidDataException("Themia.ETax: embedded font has no cmap table.");

        var result = new HashSet<int>();
        var subtableCount = ReadU16(font, cmap + 2);
        for (var i = 0; i < subtableCount; i++)
        {
            var record = cmap + 4 + (i * 8);
            var platform = ReadU16(font, record);
            var encoding = ReadU16(font, record + 2);
            var isUnicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
            if (!isUnicode)
                continue;

            var offset = cmap + (int)ReadU32(font, record + 4);
            switch (ReadU16(font, offset))
            {
                case 4: ReadFormat4(font, offset, result); break;
                case 12: ReadFormat12(font, offset, result); break;
            }
        }

        return result;
    }

    private static Dictionary<string, int> ReadTableDirectory(byte[] font)
    {
        var count = ReadU16(font, 4);
        var tables = new Dictionary<string, int>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = 12 + (i * 16);
            tables[Encoding.ASCII.GetString(font, entry, 4)] = (int)ReadU32(font, entry + 8);
        }

        return tables;
    }

    private static void ReadFormat4(byte[] font, int offset, HashSet<int> into)
    {
        var segCount = ReadU16(font, offset + 6) / 2;
        var endCodes = offset + 14;
        var startCodes = endCodes + (segCount * 2) + 2;
        var idDeltas = startCodes + (segCount * 2);
        var idRangeOffsets = idDeltas + (segCount * 2);
        for (var s = 0; s < segCount; s++)
        {
            var end = ReadU16(font, endCodes + (s * 2));
            var start = ReadU16(font, startCodes + (s * 2));
            var idDelta = ReadU16(font, idDeltas + (s * 2));
            var rangeOffset = ReadU16(font, idRangeOffsets + (s * 2));
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                var glyph = rangeOffset == 0
                    ? (c + idDelta) & 0xFFFF
                    : ReadGlyphThroughRange(font, idRangeOffsets + (s * 2), rangeOffset, c - start, idDelta);
                if (glyph != 0)
                    into.Add(c);
            }
        }
    }

    private static int ReadGlyphThroughRange(byte[] font, int rangeOffsetPosition, int rangeOffset, int index, int idDelta)
    {
        var raw = ReadU16(font, rangeOffsetPosition + rangeOffset + (index * 2));
        return raw == 0 ? 0 : (raw + idDelta) & 0xFFFF;
    }

    private static void ReadFormat12(byte[] font, int offset, HashSet<int> into)
    {
        var groups = (int)ReadU32(font, offset + 12);
        for (var g = 0; g < groups; g++)
        {
            var group = offset + 16 + (g * 12);
            var start = (int)ReadU32(font, group);
            var end = (int)ReadU32(font, group + 4);
            var firstGlyph = ReadU32(font, group + 8);
            for (var c = start; c <= end; c++)
            {
                if (firstGlyph + (uint)(c - start) != 0)
                    into.Add(c);
            }
        }
    }

    private static int ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}
