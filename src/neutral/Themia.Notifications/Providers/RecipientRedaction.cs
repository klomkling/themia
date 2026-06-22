namespace Themia.Notifications.Providers;

/// <summary>Masks a recipient (email/phone) for safe logging — keeps the last few chars for correlation.</summary>
internal static class RecipientRedaction
{
    public static string Mask(string? recipient)
    {
        if (string.IsNullOrEmpty(recipient)) return "(none)";
        return recipient.Length <= 4 ? "****" : new string('*', recipient.Length - 4) + recipient[^4..];
    }
}
