using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.Conversations.AspNetCore.Internal;

/// <summary>
/// A per-node fixed-window counter. It lives in the endpoint path rather than in <c>UseRateLimiter</c> so the limits hold whether or not the host wires
/// that middleware; the counters reset on restart and are not shared between nodes (stated in the README).
/// </summary>
/// <remarks>
/// Each scope has its own bounded table, so flooding one dimension cannot starve another. When a table is full of live buckets the oldest tenth is
/// evicted (at most once a second); if even that frees nothing, a new key is let through untracked. Memory stays bounded and a flood never locks
/// legitimate new callers out; the price is that a flood of <see cref="MaxEntries"/> distinct keys can reset other callers' counters.
/// </remarks>
internal sealed class RequestRateLimiter(TimeProvider time)
{
    internal const int MaxEntries = 10_000;
    private const int Ipv6PrefixBytes = 8;
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, Table> tables = new(StringComparer.Ordinal);

    public static IResult? Check(HttpContext context, string scope, string partition, int limit, TimeSpan window)
    {
        var limiter = context.RequestServices.GetRequiredService<RequestRateLimiter>();
        return limiter.TryAcquire(scope, partition, limit, window, out var retryAfter) ? null : Problems.TooManyRequests(context, retryAfter);
    }

    /// <summary>Refuses without counting: the caller counts only what it later learns was a failure.</summary>
    public static IResult? Peek(HttpContext context, string scope, string partition, int limit)
    {
        var limiter = context.RequestServices.GetRequiredService<RequestRateLimiter>();
        return limiter.IsOver(scope, partition, limit, out var retryAfter) ? Problems.TooManyRequests(context, retryAfter) : null;
    }

    public static void Record(HttpContext context, string scope, string partition, TimeSpan window) =>
        context.RequestServices.GetRequiredService<RequestRateLimiter>().Add(scope, partition, window);

    public static string ClientPartition(HttpContext context)
    {
        var key = context.RequestServices.GetRequiredService<IOptions<ConversationHttpOptions>>().Value.ClientKey(context);
        return string.IsNullOrEmpty(key) ? "shared" : "c:" + key;
    }

    /// <summary>An IPv6 address as its /64: one subscriber holds a whole /64, so per-address buckets would let them mint unlimited keys.</summary>
    public static string? NormalizeAddress(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, Ipv6PrefixBytes, bytes.Length - Ipv6PrefixBytes);
        return new IPAddress(bytes) + "/64";
    }

    public bool TryAcquire(string scope, string key, int limit, TimeSpan window, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        var table = TableFor(scope);
        var slot = table.Slot(key, now, window, MaxEntries);
        if (slot is null)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        lock (slot)
        {
            slot.Roll(now, window);
            retryAfter = slot.EndsAt - now;
            if (slot.Count >= limit)
            {
                return false;
            }

            slot.Count++;
            return true;
        }
    }

    public bool IsOver(string scope, string key, int limit, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        retryAfter = TimeSpan.Zero;
        if (!TableFor(scope).TryGet(key, out var slot))
        {
            return false;
        }

        lock (slot)
        {
            retryAfter = slot.EndsAt - now;
            return slot.EndsAt > now && slot.Count >= limit;
        }
    }

    public void Add(string scope, string key, TimeSpan window)
    {
        var now = time.GetUtcNow();
        var slot = TableFor(scope).Slot(key, now, window, MaxEntries);
        if (slot is null)
        {
            return;
        }

        lock (slot)
        {
            slot.Roll(now, window);
            slot.Count++;
        }
    }

    private Table TableFor(string scope) => tables.GetOrAdd(scope, _ => new Table());

    private sealed class Table
    {
        private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);
        private readonly object maintenance = new();
        private DateTimeOffset lastMaintenance;

        public bool TryGet(string key, out Window slot) => windows.TryGetValue(key, out slot!);

        /// <summary>The bucket for a key, or <see langword="null"/> when the table is full and nothing could be freed.</summary>
        public Window? Slot(string key, DateTimeOffset now, TimeSpan window, int capacity)
        {
            if (windows.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (windows.Count >= capacity && !MakeRoom(now, capacity))
            {
                return null;
            }

            return windows.GetOrAdd(key, _ => new Window(now + window));
        }

        private bool MakeRoom(DateTimeOffset now, int capacity)
        {
            lock (maintenance)
            {
                if (now - lastMaintenance >= MaintenanceInterval)
                {
                    lastMaintenance = now;
                    foreach (var expired in windows.Where(e => e.Value.EndsAt <= now).ToList())
                    {
                        windows.TryRemove(expired);
                    }

                    if (windows.Count >= capacity)
                    {
                        foreach (var oldest in windows.OrderBy(e => e.Value.EndsAt).Take(capacity / 10).ToList())
                        {
                            windows.TryRemove(oldest);
                        }
                    }
                }

                return windows.Count < capacity;
            }
        }
    }

    private sealed class Window(DateTimeOffset endsAt)
    {
        public DateTimeOffset EndsAt { get; private set; } = endsAt;

        public int Count { get; set; }

        public void Roll(DateTimeOffset now, TimeSpan window)
        {
            if (EndsAt <= now)
            {
                EndsAt = now + window;
                Count = 0;
            }
        }
    }
}
