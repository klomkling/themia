using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.Conversations.AspNetCore.Internal;

/// <summary>An authenticated request: the tenant, the actor, and whether the actor is a guest whose key the store has not yet accepted.</summary>
internal sealed record ResolvedCaller(string TenantId, ConversationActor Actor, bool IsGuest);

/// <summary>The checks every endpoint runs before it reads a body, in the order that keeps an unauthenticated caller from making the server do work.</summary>
internal static class RequestGate
{
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "themia";

    /// <summary>Every unsafe request must carry the header: the package cannot know whether the host's resolver is cookie-based, and a bearer client pays nothing for it.</summary>
    public static IResult? RequireCsrfHeader(HttpContext context) =>
        string.Equals(context.Request.Headers[CsrfHeader], CsrfValue, StringComparison.Ordinal)
            ? null
            : Problems.Of(StatusCodes.Status403Forbidden, Problems.CsrfRequired, $"The {CsrfHeader}: {CsrfValue} header is required.");

    /// <summary>The host's resolver first (a member beats a stale guest cookie), then the guest cookie for the conversation in the route; neither is a 401.</summary>
    public static async Task<(ResolvedCaller? Caller, IResult? Failure)> ResolveAsync(HttpContext context, Guid conversationId)
    {
        var resolver = context.RequestServices.GetRequiredService<IConversationActorResolver>();
        var member = await resolver.ResolveAsync(context, context.RequestAborted);
        if (member is not null)
        {
            return (new ResolvedCaller(member.TenantId, member.Actor, IsGuest: false), null);
        }

        if (GuestCookie.TryRead(context, conversationId, out var tenantId, out var key))
        {
            // The key is unproven until the store accepts it, so random well-formed keys must not each get a bucket: limit by client, not by key.
            var options = context.RequestServices.GetRequiredService<IOptions<ConversationHttpOptions>>().Value;
            if (RequestRateLimiter.Check(context, "guest", RequestRateLimiter.ClientPartition(context) + "|" + conversationId.ToString("N"), options.UnprovenGuestRequestsPerMinute, TimeSpan.FromMinutes(1)) is { } limited)
            {
                return (null, limited);
            }

            return (new ResolvedCaller(tenantId, ConversationActor.ForGuest(key!), IsGuest: true), null);
        }

        return (null, Problems.Of(StatusCodes.Status401Unauthorized, Problems.Unauthenticated, "Authentication is required."));
    }
}
