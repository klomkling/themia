namespace Themia.Payments.Beam.Internal;

/// <summary>
/// The one rule for what counts as a usable <see cref="BeamOptions.WebhookHmacKey"/>, shared by the
/// startup validation and the verifier so neither can accept a key the other rejects.
/// </summary>
internal static class BeamWebhookKey
{
    /// <summary>The shortest decoded key accepted. An empty key would let anyone forge a signature.</summary>
    public const int MinimumBytes = 16;

    /// <summary>Decodes a configured key; <see langword="false"/> when it is null, blank, not base64 or too short.</summary>
    public static bool TryDecode(string? configured, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        var buffer = new byte[configured.Length];
        if (!Convert.TryFromBase64String(configured, buffer, out var written) || written < MinimumBytes)
        {
            return false;
        }

        key = buffer[..written];
        return true;
    }
}
