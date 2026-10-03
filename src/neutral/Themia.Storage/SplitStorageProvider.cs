namespace Themia.Storage;

/// <summary>
/// Routes one <see cref="IStorageProvider"/> seam over two backends: objects whose key starts with
/// <see cref="StorageKey.PublicPrefix"/> go to the public slot, everything else to the private slot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key decides the slot.</b> Only <see cref="PutAsync"/> carries a <see cref="StoragePutOptions.Visibility"/>;
/// every other operation has just a key, so the key is the one signal there is. On a write the declared
/// visibility is a mandatory cross-check — a mismatch throws, as a single provider already does — rather than a
/// second routing input that could disagree with the key and leave an object stored but unreachable.
/// </para>
/// <para>
/// <b>The key is normalised before it is classified</b> (<see cref="StorageKey.NormalizeAndValidate"/>), and the
/// slot receives the normalised key. <c>public/../x</c> starts with <c>public/</c>, so classifying the raw key
/// would send it to the public slot. Unlike <c>LocalStorageProvider</c>, <c>S3StorageProvider</c> does not
/// normalise, so on an S3 slot a key that worked unrouted can be re-spelled or rejected here.
/// </para>
/// <para>
/// The slot receives the key unchanged otherwise (still <c>public/</c>-prefixed for the public slot), so each
/// slot's own prefix check and stripping keep working.
/// </para>
/// <para>
/// <b>Ownership.</b> A router created with this constructor owns its slots: disposing it disposes both.
/// <c>AddThemiaSplitStorage</c> does not use that: the instance form leaves the slots to the app that built them,
/// as with any instance given to the container, and the factory form never disposes them, because a factory that
/// returns a service the container already manages would otherwise see it disposed twice.
/// </para>
/// <para>
/// <b>The arguments are not interchangeable and nothing can check that you passed them the right way round.</b>
/// Only the public slot can be probed (a private slot may legitimately have a public container too), and with
/// both slots fully configured a swap silently stores public objects on the private backend and identity
/// documents on the public one. Name the arguments at the call site.
/// </para>
/// </remarks>
public sealed class SplitStorageProvider : IStorageProvider, IDisposable, IAsyncDisposable
{
    private const string PublicContainerProbeKey = StorageKey.PublicPrefix + "_probe";

    private readonly IStorageProvider publicSlot;
    private readonly IStorageProvider privateSlot;
    private readonly bool ownsSlots;

    /// <summary>Creates the router.</summary>
    /// <param name="publicSlot">Serves the keys under <see cref="StorageKey.PublicPrefix"/>. Must have a public container.</param>
    /// <param name="privateSlot">Serves every other key.</param>
    /// <exception cref="ArgumentNullException">A slot is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both slots are the same instance, so the split would be a lie.</exception>
    /// <exception cref="InvalidOperationException">The public slot has no public container configured.</exception>
    public SplitStorageProvider(IStorageProvider publicSlot, IStorageProvider privateSlot)
        : this(publicSlot, privateSlot, ownsSlots: true)
    {
    }

    internal SplitStorageProvider(IStorageProvider publicSlot, IStorageProvider privateSlot, bool ownsSlots)
    {
        ArgumentNullException.ThrowIfNull(publicSlot);
        ArgumentNullException.ThrowIfNull(privateSlot);
        if (ReferenceEquals(publicSlot, privateSlot))
        {
            throw new ArgumentException("The public and private slots must be different providers; one instance in both is not a split.", nameof(privateSlot));
        }

        ProbePublicContainer(publicSlot);
        this.publicSlot = publicSlot;
        this.privateSlot = privateSlot;
        this.ownsSlots = ownsSlots;
    }

    /// <inheritdoc />
    public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);
        StorageKey.EnsureMatchesVisibility(normalized, options.Visibility);
        return SlotFor(normalized).PutAsync(normalized, content, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);
        return SlotFor(normalized).GetAsync(normalized, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);
        return SlotFor(normalized).ExistsAsync(normalized, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);
        return SlotFor(normalized).StatAsync(normalized, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);
        return SlotFor(normalized).DeleteAsync(normalized, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);
        return SlotFor(normalized).GetPresignedUrlAsync(normalized, request, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The key is not in the public container; no slot is consulted.</exception>
    public Uri GetPublicUrl(string key)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);
        if (!StorageKey.IsPublic(normalized))
        {
            throw new InvalidOperationException(
                $"Object '{key}' is not in the public container; only a public object has a public URL. " +
                "Public visibility is chosen at write time and cannot be changed.");
        }

        return publicSlot.GetPublicUrl(normalized);
    }

    /// <summary>Disposes both slots when this router owns them; the private slot is disposed even if disposing the
    /// public one throws.</summary>
    public void Dispose()
    {
        if (!ownsSlots)
        {
            return;
        }

        try
        {
            (publicSlot as IDisposable)?.Dispose();
        }
        finally
        {
            (privateSlot as IDisposable)?.Dispose();
        }
    }

    /// <summary>As <see cref="Dispose"/>, using <see cref="IAsyncDisposable"/> where a slot has it.</summary>
    /// <returns>A task that completes when both slots are disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!ownsSlots)
        {
            return;
        }

        try
        {
            await DisposeSlotAsync(publicSlot).ConfigureAwait(false);
        }
        finally
        {
            await DisposeSlotAsync(privateSlot).ConfigureAwait(false);
        }
    }

    private static ValueTask DisposeSlotAsync(IStorageProvider slot)
    {
        if (slot is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }

        (slot as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private IStorageProvider SlotFor(string normalizedKey) =>
        StorageKey.IsPublic(normalizedKey) ? publicSlot : privateSlot;

    // GetPublicUrl is documented as pure composition with no I/O, and throws when no public container is
    // configured, so asking it about a throwaway public key fails a half-configured public slot at construction.
    private static void ProbePublicContainer(IStorageProvider slot)
    {
        try
        {
            _ = slot.GetPublicUrl(PublicContainerProbeKey);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "The public slot has no public container configured, so no public/ key could be served. " +
                "Configure its public container (for S3: PublicBucketName and PublicBaseUrl; for Local: PublicRootPath and PublicBaseUrl).",
                ex);
        }
    }
}
