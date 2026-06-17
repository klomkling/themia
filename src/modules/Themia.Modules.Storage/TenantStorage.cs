using Microsoft.Extensions.Logging;
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
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TenantStorage> logger;

    /// <summary>Creates the service.</summary>
    /// <param name="provider">The storage backend.</param>
    /// <param name="objects">The metadata repository.</param>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="tenantContext">The ambient tenant context.</param>
    /// <param name="validator">The upload validator.</param>
    /// <param name="scanner">The upload scanner.</param>
    /// <param name="options">The module options.</param>
    /// <param name="timeProvider">The time source for commit timestamps.</param>
    /// <param name="logger">The logger.</param>
    public TenantStorage(
        IStorageProvider provider,
        IRepository<StorageObject, Guid> objects,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        IFileValidator validator,
        IFileScanner scanner,
        StorageModuleOptions options,
        TimeProvider timeProvider,
        ILogger<TenantStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.provider = provider;
        this.objects = objects;
        this.unitOfWork = unitOfWork;
        this.tenantContext = tenantContext;
        this.validator = validator;
        this.scanner = scanner;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
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

        // BufferWithCapAsync leaves the buffer at position 0; pass it so a future content-sniffing
        // validator can inspect the bytes (the default validator ignores it). Reset to 0 afterward so
        // the scan/store below read from the start.
        var validation = validator.Validate(key, putOptions.ContentType, size, buffer);
        buffer.Position = 0;
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

        // Metadata-first: reserve the row (quota-checked) in a transaction, then write the blob. Server-
        // proxied uploads commit inline (the bytes are written in this call), so the row is visible at once.
        var (row, rowWasCreated, priorContentType, priorSize, priorETag) =
            await ReserveAsync(key, putOptions.ContentType, size, commit: true, cancellationToken).ConfigureAwait(false);

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
#pragma warning disable THEMIA101 // Deliberate: only the nested compensation catch logs (a distinct secondary error) before surfacing both via AggregateException; the outer catch itself just rethrows the original.
        catch (Exception primary) when (primary is not OperationCanceledException)
        {
            // Do not compensate on cancellation — the reservation row is left for a future reconcile
            // sweep. Compensation runs in its own try/catch so it can never bury the original error.
            try
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

                // Compensate with CancellationToken.None: if the request token is cancelled mid-
                // compensation, the cancel must not escape this inner catch (it would replace the
                // original `primary` error and leave the reservation row orphaned). Compensation
                // always runs to completion so `primary` is the exception that surfaces.
                await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception compensation) when (compensation is not OperationCanceledException)
            {
                logger.LogError(compensation, "Storage compensation failed for key {Key}; metadata may diverge from the blob state.", key);
                throw new AggregateException(primary, compensation);
            }

            throw;
        }
#pragma warning restore THEMIA101

        return new StoredObject(row.Id, row.Key, row.SizeBytes, row.ContentType);
    }

    /// <inheritdoc />
    public async Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key, tenantContext.CurrentTenantId, committedOnly: true), cancellationToken).ConfigureAwait(false);
        if (row is null || !InScope(row))
        {
            return null;
        }

        return await provider.GetAsync(StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        // Cannot use AnyAsync: it can't apply the in-scope check below, so fetch the row and verify scope.
        // committedOnly: a pending presigned reservation (CommittedAt == null) is invisible until completed.
        var row = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key, tenantContext.CurrentTenantId, committedOnly: true), cancellationToken).ConfigureAwait(false);
        return row is not null && InScope(row);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key, tenantContext.CurrentTenantId, committedOnly: true), cancellationToken).ConfigureAwait(false);
        if (row is null || !InScope(row))
        {
            return;
        }

        objects.Remove(row); // soft-delete (StorageObject : ISoftDeletable)
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Best-effort blob delete; a backend failure must not fail the already-committed logical
        // delete. An orphaned blob is swept by a future reconcile sweep.
        var physicalKey = StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key);
        try
        {
            await provider.DeleteAsync(physicalKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Best-effort blob delete failed for key {Key}; left for a future reconcile sweep.", key);
        }
    }

    /// <inheritdoc />
    public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default) =>
        provider.GetPresignedUrlAsync(
            StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key),
            new PresignedUrlRequest(PresignedUrlOperation.Get, expiry),
            cancellationToken);

    /// <inheritdoc />
    public async Task<Uri> GetUploadUrlAsync(string key, string contentType, long sizeBytes, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        // Presigned upload: only the declared metadata is known here, no bytes to sniff.
        var validation = validator.Validate(key, contentType, sizeBytes, content: null);
        if (!validation.IsValid)
        {
            throw new StorageValidationException(validation.Error ?? "Upload failed validation.");
        }

        // Reserve a quota-counted but PENDING metadata row up front (declared size is authoritative for
        // the reservation; CommittedAt stays null so the row is invisible to reads until CompleteUploadAsync
        // confirms it). The bytes are written by the subsequent presigned PUT; nothing here writes a blob.
        await ReserveAsync(key, contentType, sizeBytes, commit: false, cancellationToken).ConfigureAwait(false);

        return await provider.GetPresignedUrlAsync(
            StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key),
            new PresignedUrlRequest(PresignedUrlOperation.Put, expiry, contentType),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StoredObject> CompleteUploadAsync(string key, CancellationToken cancellationToken = default)
    {
        var physicalKey = StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key);

        // Stat the actually-stored bytes; absent means the client never uploaded (nothing to complete).
        var stat = await provider.StatAsync(physicalKey, cancellationToken).ConfigureAwait(false);
        if (stat is null)
        {
            throw new StorageValidationException($"No uploaded object found for key '{key}' to complete.");
        }

        var actualSize = stat.Length;

        StoredObject result = null!;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // committedOnly: false — the reservation is still pending here; load it to reconcile.
            var existingRow = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key, tenantContext.CurrentTenantId, committedOnly: false), ct).ConfigureAwait(false);
            var row = existingRow is not null && InScope(existingRow) ? existingRow : null;
            if (row is null)
            {
                throw new StorageValidationException($"No reservation to complete for key '{key}'.");
            }

            // Enforce the size cap (and any other rules) against the ACTUAL stored bytes, not the
            // declared size, so a client cannot under-declare to bypass the cap.
            var validation = validator.Validate(key, row.ContentType, actualSize, content: null);
            if (!validation.IsValid)
            {
                throw new StorageValidationException(validation.Error ?? "Upload failed validation.");
            }

            // Reconcile quota to the actual size: swap this row's reserved size for the actual size.
            var all = await objects.ListAsync(new AllStorageObjectsSpec(tenantContext.CurrentTenantId), ct).ConfigureAwait(false);
            var usage = all.Where(InScope).Sum(o => o.SizeBytes) - row.SizeBytes + actualSize;
            if (usage > options.DefaultTenantQuotaBytes)
            {
                // The actual upload overruns the quota: discard the orphaned blob (best-effort) and the
                // reservation row, then surface the quota error.
                try
                {
                    await provider.DeleteAsync(physicalKey, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Best-effort blob delete of an over-quota upload failed for key {Key}; left for a future reconcile sweep.", key);
                }

                objects.Remove(row);
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                throw new StorageQuotaExceededException(
                    $"Completing '{key}' ({actualSize} bytes) would exceed the tenant quota of {options.DefaultTenantQuotaBytes} bytes.");
            }

            row.SizeBytes = actualSize;
            row.ETag = stat.ETag;
            row.CommittedAt = timeProvider.GetUtcNow();
            objects.Update(row);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            result = new StoredObject(row.Id, row.Key, row.SizeBytes, row.ContentType);
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    // Reserves (creates or overwrites) the quota-counted metadata row for a key in a transaction.
    // When commit is true the row is marked committed (visible) inline; when false it is left PENDING
    // (CommittedAt unchanged: null on a new row, untouched on an existing one) so a presigned re-upload
    // never hides an already-committed object. Returns the row plus the prior in-scope state so a caller
    // writing a blob afterwards can compensate (restore an overwrite / remove a new reservation) if that
    // write fails.
    private async Task<(StorageObject Row, bool WasCreated, string PriorContentType, long PriorSize, string? PriorETag)> ReserveAsync(
        string key, string contentType, long size, bool commit, CancellationToken cancellationToken)
    {
        StorageObject row = null!;
        var rowWasCreated = false;
        var priorContentType = string.Empty;
        long priorSize = 0;
        string? priorETag = null;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // committedOnly: false — a pending reservation must be visible here so a re-reserve updates it
            // (rather than inserting a duplicate that hits the (tenant_id, key) unique constraint).
            var existingRow = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key, tenantContext.CurrentTenantId, committedOnly: false), ct).ConfigureAwait(false);
            var existing = existingRow is not null && InScope(existingRow) ? existingRow : null;
            var existingSize = existing?.SizeBytes ?? 0;
            var all = await objects.ListAsync(new AllStorageObjectsSpec(tenantContext.CurrentTenantId), ct).ConfigureAwait(false);
            // Sum only this scope's own rows (and subtract the in-scope existing row, captured above).
            // See InScope: EF's IncludeGlobalRecordsForTenants defaults true so the ambient filter
            // can leak platform (tenant_id IS NULL) rows into a tenant query; storage objects are
            // strictly tenant-owned, so platform bytes must never count against a tenant's quota.
            var usage = all.Where(InScope).Sum(o => o.SizeBytes) - existingSize;
            if (usage + size > options.DefaultTenantQuotaBytes)
            {
                throw new StorageQuotaExceededException(
                    $"Storing '{key}' ({size} bytes) would exceed the tenant quota of {options.DefaultTenantQuotaBytes} bytes.");
            }

            if (existing is null)
            {
                rowWasCreated = true;
                row = new StorageObject
                {
                    Key = key,
                    ContentType = contentType,
                    SizeBytes = size,
                    CommittedAt = commit ? timeProvider.GetUtcNow() : null,
                };
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
                existing.ContentType = contentType;
                existing.SizeBytes = size;
                // Commit inline (server-proxied) marks it visible; a pending presigned re-reservation
                // leaves CommittedAt unchanged so an already-committed object is never hidden mid-upload.
                if (commit)
                {
                    existing.CommittedAt = timeProvider.GetUtcNow();
                }

                objects.Update(existing);
                row = existing;
            }

            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return (row, rowWasCreated, priorContentType, priorSize, priorETag);
    }

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

    // Strict scope match: a tenant scope sees only its own rows, a platform scope (CurrentTenantId == null)
    // only platform rows. Needed because EF's ThemiaDbContext.IncludeGlobalRecordsForTenants defaults true
    // (a tenant query also returns tenant_id IS NULL rows) while Dapper defaults it false — so the ambient
    // filter alone diverges. Storage objects are strictly tenant-owned, so we enforce parity here.
    private bool InScope(StorageObject row) =>
        EqualityComparer<TenantId?>.Default.Equals(row.TenantId, tenantContext.CurrentTenantId);
}
