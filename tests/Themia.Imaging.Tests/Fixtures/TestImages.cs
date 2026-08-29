using System.Buffers.Binary;
using SkiaSharp;

namespace Themia.Imaging.Tests.Fixtures;

/// <summary>Images built byte-by-byte, so the tests assert on inputs that carry what they claim to carry.</summary>
internal static class TestImages
{
    /// <summary>Distinctive GPS latitude, so a test can search the output for it rather than trust that re-encoding drops metadata.</summary>
    internal const uint GpsLatitudeDegrees = 13;

    /// <summary>Second component of the marker latitude.</summary>
    internal const uint GpsLatitudeMinutes = 44;

    /// <summary>Third component, chosen to be a byte pattern that will not occur by chance.</summary>
    internal const uint GpsLatitudeSecondsNumerator = 567_800;

    /// <summary>
    /// A JPEG tagged Display P3 and filled with the most saturated red that gamut can express — the
    /// colour space an iPhone shoots in by default.
    /// </summary>
    internal static byte[] WideGamutRedJpeg(int width, int height)
    {
        var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, p3);

        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    /// <summary>A PNG of the given size, for the formats where a codec cannot subsample.</summary>
    internal static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(20, 120, 200));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A JPEG with an asymmetric pattern, so an orientation transform is observable in the pixels.</summary>
    internal static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(0, 0, width / 2f, height / 2f), paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    /// <summary>
    /// A JPEG carrying a real EXIF APP1 segment: the given orientation tag (1–8) and a GPS latitude.
    /// </summary>
    /// <remarks>
    /// SkiaSharp's encoder writes no EXIF, so a fixture round-tripped through it can never carry any —
    /// which is why ezy-assets could test orientation only at the unit level and could not test metadata
    /// stripping at all. Building the segment by hand is what makes both testable end to end.
    /// </remarks>
    internal static byte[] JpegWithExif(int width, int height, int orientation)
    {
        var jpeg = Jpeg(width, height);
        var app1 = BuildExifApp1(orientation);

        var result = new byte[jpeg.Length + app1.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);                        // SOI
        app1.CopyTo(result.AsSpan(2));                           // APP1 directly after it
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + app1.Length));
        return result;
    }

    /// <summary>
    /// A decompression bomb: a PNG that declares <paramref name="width"/>×<paramref name="height"/> in
    /// its header and carries no usable pixel data. **48 bytes** at 12000×12000.
    /// </summary>
    /// <remarks>
    /// This is not a contrived shape — it is the same asymmetry a solid-colour PNG has, taken to its
    /// limit: an upload byte-limit sees 48 bytes, and a decoder that reaches this file allocates
    /// 144 MB. Skia decodes it happily; nothing about the file stops it.
    /// </remarks>
    internal static byte[] HeaderOnlyPng(int width, int height)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 0;    // grayscale
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", [0x78, 0x9C, 0x01]);  // a zlib header and nothing after it
        return ms.ToArray();
    }

    private static byte[] BuildExifApp1(int orientation)
    {
        var tiff = new MemoryStream();
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); tiff.Write(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); tiff.Write(b); }

        U16(0x4949);        // little-endian
        U16(42);            // TIFF magic
        U32(8);             // IFD0 offset

        const uint entrySize = 12;
        const uint ifdSize = 2 + (2 * entrySize) + 4;   // count + two entries + next-IFD offset
        const uint gpsIfdOffset = 8 + ifdSize;
        const uint gpsDataOffset = gpsIfdOffset + ifdSize;

        U16(2);                                                                     // IFD0, two entries
        U16(0x0112); U16(3); U32(1); U16((ushort)orientation); U16(0);              // Orientation (SHORT, inline)
        U16(0x8825); U16(4); U32(1); U32(gpsIfdOffset);                             // GPS IFD pointer
        U32(0);                                                                     // no IFD1

        U16(2);                                                                     // GPS IFD, two entries
        U16(0x0001); U16(2); U32(2); tiff.WriteByte((byte)'N'); tiff.WriteByte(0); U16(0);  // GPSLatitudeRef
        U16(0x0002); U16(5); U32(3); U32(gpsDataOffset);                            // GPSLatitude (3 rationals)
        U32(0);

        U32(GpsLatitudeDegrees); U32(1);
        U32(GpsLatitudeMinutes); U32(1);
        U32(GpsLatitudeSecondsNumerator); U32(10_000);

        var tiffBytes = tiff.ToArray();
        var payload = new byte[6 + tiffBytes.Length];
        "Exif\0\0"u8.CopyTo(payload);
        tiffBytes.CopyTo(payload, 6);

        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return segment;
    }

    private static void WriteChunk(Stream s, string type, byte[] payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        s.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(payload);

        var crcInput = new byte[typeBytes.Length + payload.Length];
        typeBytes.CopyTo(crcInput, 0);
        payload.CopyTo(crcInput, typeBytes.Length);
        s.Write(Crc32(crcInput));
    }

    private static byte[] Crc32(byte[] data)
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        crc ^= 0xFFFFFFFFu;
        var result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, crc);
        return result;
    }
}
