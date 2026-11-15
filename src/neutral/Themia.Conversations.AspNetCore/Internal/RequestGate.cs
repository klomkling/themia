using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.Conversations.AspNetCore.Internal;

/// <summary>An authenticated request: the tenant, the actor, and whether the actor is a guest whose key the store has not yet accepted.</summary>
internal sealed record ResolvedCaller(string TenantId, ConversationActor Actor, bool IsGuest, RateTicket GuestTicket = default);

/// <summary>The checks every endpoint runs before it reads a body, in the order that keeps an unauthenticated caller from making the server do work.</summary>
internal static class RequestGate
{
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "themia";
    private const string GuestScope = "guest-unproven";
    private static readonly TimeSpan GuestWindow = TimeSpan.FromMinutes(1);

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
            // The slot is taken here, before any store call, so a burst of concurrent guesses is counted; RefundAcceptedGuest gives it back once the store accepts the key.
            var options = context.RequestServices.GetRequiredService<IOptions<ConversationHttpOptions>>().Value;
            if (RequestRateLimiter.Check(context, GuestScope, RequestRateLimiter.ClientPartition(context), options.UnprovenGuestRequestsPerMinute, GuestWindow, out var ticket) is { } limited)
            {
                return (null, limited);
            }

            return (new ResolvedCaller(tenantId, ConversationActor.ForGuest(key!), IsGuest: true, ticket), null);
        }

        return (null, Problems.Of(StatusCodes.Status401Unauthorized, Problems.Unauthenticated, "Authentication is required."));
    }

    /// <summary>Refunds the unproven-guest slot once the store accepted the key: only refused or invalid guest requests stay counted.</summary>
    public static void RefundAcceptedGuest(HttpContext context, ResolvedCaller caller)
    {
        if (caller.IsGuest)
        {
            RequestRateLimiter.Refund(context, caller.GuestTicket);
        }
    }
}
