using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.Conversations.AspNetCore.Internal;

/// <summary>The slot a request took, bound to its bucket and window so a refund can never land anywhere else.</summary>
internal readonly record struct RateTicket(RequestRateLimiter.Window Slot, DateTimeOffset EndsAt);

/// <summary>
/// A per-node fixed-window counter. It lives in the endpoint path rather than in <c>UseRateLimiter</c> so the limits hold whether or not the host wires
/// that middleware; the counters reset on restart and are not shared between nodes (stated in the README).
/// </summary>
/// <remarks>
/// Each scope has its own bounded table, so flooding one dimension cannot starve another. When a table is full the expired buckets go first, then the
/// oldest tenth of those still under their limit (a bucket at its limit is never evicted, so a flood cannot reset an offender). Keys that still find no
/// room share one overflow bucket per scope, <see cref="OverflowFactor"/> times the scope's limit: bounded memory, and a flood throttles new keys
/// collectively instead of passing them free or refusing them outright.
/// </remarks>
internal sealed class RequestRateLimiter(TimeProvider time)
{
    internal const int MaxEntries = 10_000;
    internal const int OverflowFactor = 10;
    private const int Ipv6PrefixBytes = 8;
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, Table> tables = new(StringComparer.Ordinal);

    public static IResult? Check(HttpContext context, string scope, string partition, int limit, TimeSpan window) =>
        Check(context, scope, partition, limit, window, out _);

    public static IResult? Check(HttpContext context, string scope, string partition, int limit, TimeSpan window, out RateTicket ticket)
    {
        var limiter = context.RequestServices.GetRequiredService<RequestRateLimiter>();
        return limiter.TryAcquire(scope, partition, limit, window, out var retryAfter, out ticket) ? null : Problems.TooManyRequests(context, retryAfter);
    }

    public static void Refund(HttpContext context, RateTicket ticket) =>
        context.RequestServices.GetRequiredService<RequestRateLimiter>().Refund(ticket);

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

    public bool TryAcquire(string scope, string key, int limit, TimeSpan window, out TimeSpan retryAfter) =>
        TryAcquire(scope, key, limit, window, out retryAfter, out _);

    public bool TryAcquire(string scope, string key, int limit, TimeSpan window, out TimeSpan retryAfter, out RateTicket ticket)
    {
        var now = time.GetUtcNow();
        var table = TableFor(scope);
        var slot = table.Slot(key, now, window, MaxEntries);
        if (slot is null)
        {
            slot = table.Overflow;
            limit = checked(limit * OverflowFactor);
        }

        lock (slot)
        {
            slot.Roll(now, window);
            slot.Limit = limit;
            retryAfter = slot.EndsAt - now;
            if (slot.Count >= limit)
            {
                ticket = default;
                return false;
            }

            slot.Count++;
            ticket = new RateTicket(slot, slot.EndsAt);
            return true;
        }
    }

    /// <summary>Gives back the slot a ticket took, only while its bucket is still in the window it was charged in: a refund never lands in a later window or another bucket.</summary>
    public void Refund(RateTicket ticket)
    {
        if (ticket.Slot is null)
        {
            return;
        }

        lock (ticket.Slot)
        {
            if (ticket.Slot.EndsAt == ticket.EndsAt && ticket.Slot.Count > 0)
            {
                ticket.Slot.Count--;
            }
        }
    }

    private Table TableFor(string scope) => tables.GetOrAdd(scope, _ => new Table());

    private sealed class Table
    {
        private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);
        private readonly object maintenance = new();
        public Window Overflow { get; } = new(DateTimeOffset.MinValue);
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
                        foreach (var oldest in windows.Where(e => !e.Value.IsAtLimit).OrderBy(e => e.Value.EndsAt).Take(capacity / 10).ToList())
                        {
                            windows.TryRemove(oldest);
                        }
                    }
                }

                return windows.Count < capacity;
            }
        }
    }

    internal sealed class Window(DateTimeOffset endsAt)
    {
        public DateTimeOffset EndsAt { get; private set; } = endsAt;

        public int Count { get; set; }

        public int Limit { get; set; } = int.MaxValue;

        public bool IsAtLimit
        {
            get
            {
                lock (this)
                {
                    return Count >= Limit;
                }
            }
        }

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
