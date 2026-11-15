using Microsoft.AspNetCore.Http;

namespace Themia.Conversations.AspNetCore;

/// <summary>Limits and keys for the conversation endpoints.</summary>
public sealed class ConversationHttpOptions
{
    /// <summary>
    /// Identifies the client for the exchange and unproven-guest rate limits. Behind a reverse proxy <see cref="ConnectionInfo.RemoteIpAddress"/> is the
    /// proxy, so the default makes every caller one bucket: supply the real client address from the host's own resolver. Returning
    /// <see langword="null"/> puts the request in one shared bucket (fails closed). Default: the remote IP address.
    /// </summary>
    public Func<HttpContext, string?> ClientKey { get; set; } = DefaultClientKey;

    internal static string? DefaultClientKey(HttpContext context) => Internal.RequestRateLimiter.NormalizeAddress(context.Connection.RemoteIpAddress);

    /// <summary>Posts per participant per conversation per minute. Default 30.</summary>
    public int PostsPerMinute { get; set; } = 30;

    /// <summary>Guest link exchanges per client per hour. Default 20.</summary>
    public int ExchangesPerHour { get; set; } = 20;

    /// <summary>Guest requests per client per minute before the session key has been accepted by the store. Default 60.</summary>
    public int UnprovenGuestRequestsPerMinute { get; set; } = 60;

    /// <summary>Largest JSON request body in bytes. Default 64 KiB.</summary>
    public int MaxJsonBodyBytes { get; set; } = 64 * 1024;
}
