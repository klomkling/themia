using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Themia.Storage.S3;
using Themia.Storage.Urls;

namespace Themia.Storage.Configuration;

/// <summary>Registers the app's one <see cref="IStorageProvider"/> from the <c>Storage</c> configuration section.</summary>
public static class StorageConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Reads the <c>Storage</c> section and registers <see cref="IStorageProvider"/> and
    /// <see cref="Urls.IStorageUrlService"/> for it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding the <c>Storage</c> section.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Private files (every key outside <c>public/</c>) are always on Local: nothing in the configuration can move
    /// them, and a value that tries (<c>Storage:Private:Provider=S3</c>, <c>Storage:Provider=S3</c>, a typo) stops the
    /// host at start. The public slot is Local, or S3/R2 with <c>Storage:Public:Provider</c>; with S3/R2 the
    /// provider is a <see cref="SplitStorageProvider"/> whose S3 side is public-only. The key set and the deprecated
    /// keys still read for one release are in the package README.
    /// <para>
    /// The configuration is read when the host starts (a bad one stops it), so a relative path is resolved against the
    /// content root and test overrides of the configuration apply.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">An <see cref="IStorageProvider"/> is already registered.</exception>
    public static IServiceCollection AddThemiaStorageFromConfiguration(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(d => d.ServiceType == typeof(IStorageProvider) && !d.IsKeyedService))
        {
            throw new InvalidOperationException(
                "An IStorageProvider is already registered; an app has exactly one. AddThemiaStorageFromConfiguration registers it.");
        }

        services.AddSingleton(sp =>
        {
            var settings = StorageSettingsReader.Read(configuration, sp.GetRequiredService<IHostEnvironment>());
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Themia.Storage.Configuration");
            foreach (var warning in settings.Warnings)
            {
                logger.LogWarning("{Warning} Deprecated keys will stop being read in a later release; switch to the new names.", warning);
            }

            return settings;
        });
        services.AddSingleton(sp => CreateProvider(sp.GetRequiredService<StorageSettings>(), sp.GetService<ICdnPurger>()));

        // MapThemiaLocalStorage verifies presigned tokens with this signer; it must use the provider's SigningKey.
        // TryAdd: an app that registers its own signer keeps it.
        services.TryAddSingleton(sp => new LocalUrlSigner(sp.GetRequiredService<StorageSettings>().Local.SigningKey));

        // The router (or the Local provider) is what apps inject; the container disposes it, and it disposes its slots.
        services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<ConfiguredStorage>().Provider);

        services.AddThemiaStorageUrls();
        services.AddOptions<StorageUrlOptions>().Configure<StorageSettings>((options, settings) =>
        {
            if (settings.PresignedBaseUrl.Length > 0)
            {
                options.PresignedBaseUrl = settings.PresignedBaseUrl;
            }
        });

        services.AddOptions<StorageConfigurationStartupCheck>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<StorageConfigurationStartupCheck>, StorageConfigurationStartupValidator>());
        return services;
    }

    private static ConfiguredStorage CreateProvider(StorageSettings settings, ICdnPurger? purger)
    {
        if (settings.Public == PublicSlot.Local)
        {
            return new ConfiguredStorage(new LocalStorageProvider(settings.Local), ownedS3: null);
        }

        // Slots are passed by name: the router cannot tell a swapped pair. The S3 slot is public-only, so it can
        // only ever hold public objects; the Local slot has no public container.
        var s3 = new S3StorageProvider(settings.S3!);
        try
        {
            // With a purger the public slot is wrapped, and the wrapper is not disposable, so the router would leak
            // the S3 client: ConfiguredStorage disposes it instead. Unwrapped, the router owns and disposes it.
            IStorageProvider publicSlot = purger is null ? s3 : new PurgingStorageProvider(s3, purger);
            return new ConfiguredStorage(
                new SplitStorageProvider(publicSlot, new LocalStorageProvider(settings.Local)),
                ownedS3: purger is null ? null : s3);
        }
        catch
        {
            s3.Dispose();
            throw;
        }
    }

    /// <summary>The built provider, held so the start-up check can tell it from a later registration. The container
    /// disposes the provider once, as the singleton <see cref="IStorageProvider"/>; this disposes only an S3 client the
    /// router does not own (the public slot wrapped by <see cref="PurgingStorageProvider"/>).</summary>
    internal sealed class ConfiguredStorage(IStorageProvider provider, S3StorageProvider? ownedS3) : IDisposable
    {
        public IStorageProvider Provider { get; } = provider;

        public void Dispose() => ownedS3?.Dispose();
    }

    /// <summary>The empty options type whose validation builds the provider at host start.</summary>
    internal sealed class StorageConfigurationStartupCheck
    {
    }

    internal sealed class StorageConfigurationStartupValidator(IServiceProvider services) : IValidateOptions<StorageConfigurationStartupCheck>
    {
        public ValidateOptionsResult Validate(string? name, StorageConfigurationStartupCheck options)
        {
            try
            {
                var built = services.GetRequiredService<ConfiguredStorage>().Provider;
                var provider = services.GetRequiredService<IStorageProvider>();

                // A later registration of another provider would silently replace this one.
                return ReferenceEquals(provider, built)
                    ? ValidateOptionsResult.Success
                    : ValidateOptionsResult.Fail(
                        $"AddThemiaStorageFromConfiguration was used, but IStorageProvider resolves to {provider.GetType().Name}: a later registration replaced it.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }
        }
    }
}
