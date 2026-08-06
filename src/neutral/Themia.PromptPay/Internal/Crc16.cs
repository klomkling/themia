using System.Globalization;

namespace Themia.PromptPay.Internal;

/// <summary>CRC-16/CCITT-FALSE, the checksum EMVCo tag 63 carries.</summary>
/// <remarks>
/// Polynomial <c>0x1021</c>, initial value <c>0xFFFF</c>, no reflection, no final XOR, emitted as four
/// uppercase hex digits. Written bitwise rather than as a lookup table: the table form is where a
/// transcription error hides silently, and this runs once per QR rather than in any hot path.
/// <para>
/// <b>Not "CRC-16" generically.</b> The initial value is what distinguishes this from XMODEM (which
/// starts at <c>0x0000</c> with the same polynomial) and from CCITT variants that reflect their input.
/// Substituting any of those produces four plausible hex digits that no bank app accepts.
/// </para>
/// </remarks>
internal static class Crc16
{
    private const ushort Polynomial = 0x1021;
    private const ushort InitialValue = 0xFFFF;

    /// <summary>Computes the checksum over <paramref name="payload"/>'s bytes.</summary>
    /// <param name="payload">The ASCII payload, including the checksum tag's own id and length.</param>
    /// <returns>Four uppercase hex digits.</returns>
    internal static string Compute(string payload)
    {
        var crc = InitialValue;

        foreach (var character in payload)
        {
            // A non-ASCII character has no defined single-byte encoding here, and shifting it into the
            // register would produce four plausible hex digits over the wrong bytes — a checksum that
            // looks computed and is not. EMVCo payloads are ASCII, so this is unreachable unless a
            // caller-supplied field slipped past validation, which is exactly when silence is worst.
            if (character > 0x7F)
            {
                throw new ArgumentException(
                    $"Payload contains the non-ASCII character U+{(int)character:X4}; an EMVCo QR payload is ASCII.",
                    nameof(payload));
            }

            crc ^= (ushort)(character << 8);

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ Polynomial)
                    : (ushort)(crc << 1);
            }
        }

        return crc.ToString("X4", CultureInfo.InvariantCulture);
    }
}
