using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Storage.Entities;
using Themia.Modules.Storage.Scanning;
using Themia.Modules.Storage.Specifications;
using Themia.Modules.Storage.Validation;
using Themia.Storage;

namespace Themia.Modules.Storage;

/// <summary>Default <see cref="ITenantStorage"/>. Prefixes every key with the ambient tenant
/// (<see cref="StorageScope"/>), validates + scans uploads, enforces per-tenant quota transactionally
/// (metadata-first), and stores the blob via the configured <see cref="IStorageProvider"/>.</summary>
public sealed class TenantStorage : ITenantStorage
{
    private readonly IStorageProvider provider;
    private readonly IRepository<StorageObject, Guid> objects;
    private readonly IUnitOfWork unitOfWork;
    private readonly ITenantContext tenantContext;
    private readonly IFileValidator validator;
    private readonly IFileScanner scanner;
    private readonly StorageModuleOptions options;

    /// <summary>Creates the service.</summary>
    /// <param name="provider">The storage backend.</param>
    /// <param name="objects">The metadata repository.</param>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="tenantContext">The ambient tenant context.</param>
    /// <param name="validator">The upload validator.</param>
    /// <param name="scanner">The upload scanner.</param>
    /// <param name="options">The module options.</param>
    public TenantStorage(
        IStorageProvider provider,
        IRepository<StorageObject, Guid> objects,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        IFileValidator validator,
        IFileScanner scanner,
        StorageModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(options);
        this.provider = provider;
        this.objects = objects;
        this.unitOfWork = unitOfWork;
        this.tenantContext = tenantContext;
        this.validator = validator;
        this.scanner = scanner;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<StoredObject> PutAsync(string key, Stream content, StoragePutOptions putOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var physicalKey = StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key);

        // Buffer + measure with a hard cap so the size limit holds even for a non-seekable stream
        // (server-proxied uploads are for small files; large uploads use a presigned PUT).
        var buffer = await BufferWithCapAsync(content, options.MaxObjectSizeBytes, cancellationToken).ConfigureAwait(false);
        var size = buffer.Length;

        var validation = validator.Validate(key, putOptions.ContentType, size);
        if (!validation.IsValid)
        {
            throw new StorageValidationException(validation.Error ?? "Upload failed validation.");
        }

        var scan = await scanner.ScanAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        if (!scan.IsClean)
        {
            throw new StorageScanException(scan.Threat ?? "Upload failed the virus scan.");
        }

        // Metadata-first: reserve the row (quota-checked) in a transaction, then write the blob.
        StorageObject row = null!;
        var rowWasCreated = false;
        var priorContentType = string.Empty;
        long priorSize = 0;
        string? priorETag = null;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var existing = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key), ct).ConfigureAwait(false);
            var existingSize = existing?.SizeBytes ?? 0;
            var all = await objects.ListAsync(new AllStorageObjectsSpec(), ct).ConfigureAwait(false);
            var usage = all.Sum(o => o.SizeBytes) - existingSize;
            if (usage + size > options.DefaultTenantQuotaBytes)
            {
                throw new StorageQuotaExceededException(
                    $"Storing '{key}' ({size} bytes) would exceed the tenant quota of {options.DefaultTenantQuotaBytes} bytes.");
            }

            if (existing is null)
            {
                rowWasCreated = true;
                row = new StorageObject { Key = key, ContentType = putOptions.ContentType, SizeBytes = size };
                row.SetId(Guid.CreateVersion7());
                await objects.AddAsync(row, ct).ConfigureAwait(false);
            }
            else
            {
                // Capture the prior metadata so a failed blob write can be rolled back without
                // soft-deleting the user's pre-existing object (whose old blob is still present).
                priorContentType = existing.ContentType;
                priorSize = existing.SizeBytes;
                priorETag = existing.ETag;
                existing.ContentType = putOptions.ContentType;
                existing.SizeBytes = size;
                objects.Update(existing);
                row = existing;
            }

            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var info = await provider.PutAsync(physicalKey, buffer, putOptions, cancellationToken).ConfigureAwait(false);
            if (info.ETag is not null)
            {
                row.ETag = info.ETag;
                objects.Update(row);
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (rowWasCreated)
            {
                // Compensate: the reservation row was created this call but the blob write failed —
                // remove it (soft-delete) so no metadata is left without a blob.
                objects.Remove(row);
            }
            else
            {
                // Overwrite case: restore (don't soft-delete) the pre-existing object's prior metadata.
                // Deleting it would silently lose the user's object whose previous blob is still present.
                row.ContentType = priorContentType;
                row.SizeBytes = priorSize;
                row.ETag = priorETag;
                objects.Update(row);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        return new StoredObject(row.Id, row.Key, row.SizeBytes, row.ContentType);
    }

    /// <inheritdoc />
    public async Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key), cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return await provider.GetAsync(StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        objects.AnyAsync(new StorageObjectByKeySpec(key), cancellationToken);

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key), cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        objects.Remove(row); // soft-delete (StorageObject : ISoftDeletable)
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Best-effort blob delete; an orphaned blob is swept by the 0.5.5 reconcile job.
        await provider.DeleteAsync(StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default) =>
        provider.GetPresignedUrlAsync(
            StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key),
            new PresignedUrlRequest(PresignedUrlOperation.Get, expiry),
            cancellationToken);

    /// <inheritdoc />
    public Task<Uri> GetUploadUrlAsync(string key, string contentType, TimeSpan expiry, CancellationToken cancellationToken = default) =>
        provider.GetPresignedUrlAsync(
            StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key),
            new PresignedUrlRequest(PresignedUrlOperation.Put, expiry, contentType),
            cancellationToken);

    private static async Task<MemoryStream> BufferWithCapAsync(Stream source, long cap, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                throw new StorageValidationException($"Object exceeds the maximum size of {cap} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}
