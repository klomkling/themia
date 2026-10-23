using Microsoft.Extensions.Caching.Memory;

namespace Themia.Modules.Identity.Services;

/// <summary>The validator's per-user cache entry: one key scheme shared by the reader and every rotation path, so a
/// rotation in this process evicts what the validator cached (other instances still wait out the window).</summary>
internal static class SecurityStampCache
{
    private const string KeyPrefix = "themia:identity:stamp:";

    public static string Key(Guid userId) => KeyPrefix + userId.ToString("N");

    public static MemoryCacheEntryOptions EntryOptions(TimeSpan window) =>
        new() { AbsoluteExpirationRelativeToNow = window, Size = 1 };   // Size: an adopter cache may set SizeLimit

    /// <summary>Evicts the cached stamp of each user. Call after the rotation is committed, so a request cannot
    /// re-cache the old stamp between the eviction and the commit.</summary>
    public static void Evict(IMemoryCache? cache, IEnumerable<Guid> userIds)
    {
        if (cache is null)
        {
            return;
        }

        foreach (var userId in userIds)
        {
            cache.Remove(Key(userId));
        }
    }

    public static string NewStamp() => Guid.NewGuid().ToString("N");
}
