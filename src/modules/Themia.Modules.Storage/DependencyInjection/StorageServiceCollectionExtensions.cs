using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Storage.Mapping;
using Themia.Modules.Storage.Scanning;
using Themia.Modules.Storage.Validation;
using Themia.Storage;
using Themia.Storage.Local;
using Themia.Storage.S3;

namespace Themia.Modules.Storage.DependencyInjection;

/// <summary>Registers the Themia Storage module: the tenant storage service, validation/scan seams, and
/// a fluent backend builder (<see cref="StorageBuilder.UseLocal"/> / <see cref="StorageBuilder.UseS3"/> /
/// <see cref="StorageBuilder.UseR2"/>).</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>Registers the storage services and returns a builder to select exactly one backend.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the module options.</param>
    /// <returns>The storage builder.</returns>
    public static StorageBuilder AddThemiaStorage(this IServiceCollection services, Action<StorageModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new StorageModuleOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(options);

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFileValidator, DefaultFileValidator>();
        services.TryAddSingleton<IFileScanner, NullFileScanner>();
        services.TryAddScoped<ITenantStorage, TenantStorage>();

        // Dapper adopters: contribute the StorageObject mapping to the registry they already registered
        // (mirrors AddThemiaIdentityServices.ContributeDapperMappings). No-op when EF is the peer.
        ContributeDapperMappings(services);

        return new StorageBuilder(services);
    }

    // Mirror Identity: scan the collection for the already-registered EntityMappingRegistry singleton
    // instance and apply the Storage mappings to it. No service provider is built.
    private static void ContributeDapperMappings(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(EntityMappingRegistry)
                && services[i].ImplementationInstance is EntityMappingRegistry registry)
            {
                StorageDapperMappings.Apply(registry);
                return;
            }
        }
    }
}

/// <summary>A fluent builder for selecting the storage backend. Exactly one backend may be registered.</summary>
public sealed class StorageBuilder
{
    private readonly IServiceCollection services;

    internal StorageBuilder(IServiceCollection services) => this.services = services;

    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services => services;

    /// <summary>Uses the Local filesystem backend.</summary>
    /// <param name="configure">Configures the Local options.</param>
    /// <returns>The same builder.</returns>
    public StorageBuilder UseLocal(Action<LocalStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var local = new LocalStorageOptions();
        configure(local);
        local.Validate();
        // Register the options so MapThemiaStorageEndpoints can validate the public mount path at startup.
        services.TryAddSingleton(local);
        // Register the signer so the module's _local routes can verify presigned tokens.
        if (!string.IsNullOrWhiteSpace(local.SigningKey))
        {
            services.AddSingleton(new LocalUrlSigner(local.SigningKey));
        }

        return RegisterProvider(_ => new LocalStorageProvider(local));
    }

    /// <summary>Uses an S3-compatible backend.</summary>
    /// <param name="configure">Configures the S3 options.</param>
    /// <returns>The same builder.</returns>
    public StorageBuilder UseS3(Action<S3StorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var s3 = new S3StorageOptions();
        configure(s3);
        return RegisterProvider(_ => new S3StorageProvider(s3));
    }

    /// <summary>Uses Cloudflare R2 (S3-compatible: sets the R2 endpoint + path-style addressing).</summary>
    /// <param name="configure">Configures the R2 options.</param>
    /// <returns>The same builder.</returns>
    public StorageBuilder UseR2(Action<R2StorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var r2 = new R2StorageOptions();
        configure(r2);
        ArgumentException.ThrowIfNullOrWhiteSpace(r2.AccountId);
        var s3 = new S3StorageOptions
        {
            BucketName = r2.BucketName,
            AccessKey = r2.AccessKey,
            SecretKey = r2.SecretKey,
            ServiceUrl = new Uri($"https://{r2.AccountId}.r2.cloudflarestorage.com"),
            ForcePathStyle = true,
        };
        return RegisterProvider(_ => new S3StorageProvider(s3));
    }

    private StorageBuilder RegisterProvider(Func<IServiceProvider, IStorageProvider> factory)
    {
        if (services.Any(d => d.ServiceType == typeof(IStorageProvider)))
        {
            throw new InvalidOperationException("A storage backend is already registered; configure exactly one of UseLocal/UseS3/UseR2.");
        }

        services.AddSingleton(factory);
        return this;
    }
}

/// <summary>Cloudflare R2 credentials (an S3-compatible backend addressed by account id).</summary>
public sealed class R2StorageOptions
{
    /// <summary>The Cloudflare account id (forms the R2 endpoint host).</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>The R2 bucket name.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>The R2 access key id.</summary>
    public string? AccessKey { get; set; }

    /// <summary>The R2 secret access key.</summary>
    public string? SecretKey { get; set; }
}
