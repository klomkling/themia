using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Themia.Conversations.AspNetCore.Internal;

/// <summary>
/// The guest session cookie, one per conversation: <c>__Host-themia_conv_{id:N}</c> holding base64url of <c>{"t":tenant,"k":sessionKey}</c>. The value is a claim, not a
/// credential the package vouches for: the store joins key hash, tenant and conversation on every call, so a tampered or swapped value matches nothing.
/// </summary>
internal static class GuestCookie
{
    internal const int MaxTenantLength = 100;
    // A 100-char tenant of non-ASCII text serializes to 600 bytes of \uXXXX escapes, so ~890 base64url chars with the key; 512 locked such a guest out of their own cookie.
    private const int MaxValueLength = 1024;
    private const string NamePrefix = "__Host-themia_conv_";

    public static string NameFor(Guid conversationId) => NamePrefix + conversationId.ToString("N");

    public static bool TryRead(HttpContext context, Guid conversationId, out string tenantId, out GuestSessionKey? key)
    {
        tenantId = string.Empty;
        key = null;
        if (!context.Request.Cookies.TryGetValue(NameFor(conversationId), out var raw) || string.IsNullOrEmpty(raw) || raw.Length > MaxValueLength)
        {
            return false;
        }

        try
        {
            var json = WebEncoders.Base64UrlDecode(raw);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("t", out var tenant) || tenant.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("k", out var keyValue) || keyValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var tenantText = tenant.GetString();
            if (!IsBoundedTenant(tenantText) || !GuestSessionKey.TryParse(keyValue.GetString(), out key))
            {
                return false;
            }

            tenantId = tenantText!;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public static void Write(HttpContext context, Guid conversationId, string tenantId, string sessionKey, DateTimeOffset expiresAt)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["t"] = tenantId, ["k"] = sessionKey });
        context.Response.Cookies.Append(NameFor(conversationId), WebEncoders.Base64UrlEncode(payload), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAt,
            IsEssential = true,
        });
    }

    /// <summary>Length and character bound applied before any store call, so a hostile cookie costs no database round trip.</summary>
    internal static bool IsBoundedTenant(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxTenantLength && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
