namespace Themia.Storage;

/// <summary>Shared syntactic validation for object keys, used by both the neutral provider and the
/// tenant-scoping module so the traversal/absolute-path rejection stays in one place.</summary>
public static class StorageKey
{
    /// <summary>The reserved first segment marking a physical key as living in the public container.
    /// A routing marker only: it is stripped before the key reaches the container, so it never appears in
    /// a stored key or a public URL. Private keys are deliberately left unprefixed, so every object
    /// written before this feature keeps its exact key and no blob has to move.</summary>
    public const string PublicPrefix = "public/";

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

    /// <summary>Whether <paramref name="key"/> addresses the public container.</summary>
    /// <param name="key">The physical object key.</param>
    /// <returns><see langword="true"/> when the key's first segment is the reserved public prefix.</returns>
    public static bool IsPublic(string key) =>
        key is not null && key.StartsWith(PublicPrefix, StringComparison.Ordinal);

    /// <summary>Removes the visibility prefix, yielding the key as the container stores it.</summary>
    /// <param name="key">The physical object key.</param>
    /// <returns>The key without its <see cref="PublicPrefix"/>; an unprefixed key is returned unchanged.</returns>
    public static string StripVisibilityPrefix(string key) =>
        IsPublic(key) ? key[PublicPrefix.Length..] : key;
}
