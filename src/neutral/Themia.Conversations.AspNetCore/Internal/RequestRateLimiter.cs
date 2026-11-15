using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.Conversations.AspNetCore.Internal;

/// <summary>
/// A per-node fixed-window counter. It lives in the endpoint path rather than in <c>UseRateLimiter</c> so the limits hold whether or not the host wires
/// that middleware; the counters reset on restart and are not shared between nodes (stated in the README).
/// </summary>
internal sealed class RequestRateLimiter(TimeProvider time)
{
    internal const int MaxEntries = 10_000;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);
    private readonly object pruneLock = new();
    private DateTimeOffset lastPrune;

    public static IResult? Check(HttpContext context, string scope, string partition, int limit, TimeSpan window)
    {
        var limiter = context.RequestServices.GetRequiredService<RequestRateLimiter>();
        return limiter.TryAcquire(scope + "|" + partition, limit, window, out var retryAfter) ? null : Problems.TooManyRequests(context, retryAfter);
    }

    public static string ClientPartition(HttpContext context)
    {
        var key = context.RequestServices.GetRequiredService<IOptions<ConversationHttpOptions>>().Value.ClientKey(context);
        return string.IsNullOrEmpty(key) ? "shared" : "c:" + key;
    }

    public bool TryAcquire(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        if (windows.Count >= MaxEntries && !windows.ContainsKey(key) && !MakeRoom(now))
        {
            // Full of live buckets: fail closed for a new key rather than grow without bound or rescan on every call.
            retryAfter = window;
            return false;
        }

        var slot = windows.GetOrAdd(key, _ => new Window(now + window));
        lock (slot)
        {
            if (slot.EndsAt <= now)
            {
                slot.EndsAt = now + window;
                slot.Count = 0;
            }

            retryAfter = slot.EndsAt - now;
            if (slot.Count >= limit)
            {
                return false;
            }

            slot.Count++;
            return true;
        }
    }

    private bool MakeRoom(DateTimeOffset now)
    {
        lock (pruneLock)
        {
            if (now - lastPrune >= PruneInterval)
            {
                lastPrune = now;
                foreach (var entry in windows.Where(e => e.Value.EndsAt <= now))
                {
                    windows.TryRemove(entry);
                }
            }

            return windows.Count < MaxEntries;
        }
    }

    private sealed class Window(DateTimeOffset endsAt)
    {
        public DateTimeOffset EndsAt { get; set; } = endsAt;

        public int Count { get; set; }
    }
}
