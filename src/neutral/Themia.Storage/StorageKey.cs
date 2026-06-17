namespace Themia.Storage;

/// <summary>Shared syntactic validation for object keys, used by both the neutral provider and the
/// tenant-scoping module so the traversal/absolute-path rejection stays in one place.</summary>
public static class StorageKey
{
    /// <summary>Normalizes <paramref name="key"/> (back-slashes to forward-slashes) and rejects unsafe keys.</summary>
    /// <param name="key">The object key.</param>
    /// <returns>The normalized key.</returns>
    /// <exception cref="System.ArgumentException">The key is blank, absolute (leading '/'), or contains a
    /// '..' path segment.</exception>
    public static string NormalizeAndValidate(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Contains(".."))
        {
            throw new ArgumentException($"Invalid object key '{key}': absolute paths and '..' segments are not allowed.", nameof(key));
        }

        return normalized;
    }
}
