using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.Storage;

/// <summary>Registers <see cref="SplitStorageProvider"/> as the app's single <see cref="IStorageProvider"/>.</summary>
public static class SplitStorageServiceCollectionExtensions
{
    /// <summary>Registers a router over two slot instances you have already built.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="publicSlot">Serves the keys under <see cref="StorageKey.PublicPrefix"/>.</param>
    /// <param name="privateSlot">Serves every other key.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// The router is built here, so a bad pair (same instance twice, a public slot with no public container) fails
    /// at registration, before the host runs. Each slot's own constructor validates what it validates:
    /// <c>S3StorageProvider</c> checks its bucket names, but <c>LocalStorageProvider</c> does not call
    /// <c>LocalStorageOptions.Validate()</c>, so a Local slot should have it called before it is constructed.
    /// </remarks>
    /// <exception cref="InvalidOperationException">An <see cref="IStorageProvider"/> is already registered.</exception>
    public static IServiceCollection AddThemiaSplitStorage(
        this IServiceCollection services, IStorageProvider publicSlot, IStorageProvider privateSlot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ThrowIfAProviderIsRegistered(services);

        var router = new SplitStorageProvider(publicSlot, privateSlot);

        // A factory returning the prebuilt router, not AddSingleton(instance): the container disposes what a
        // factory creates, and disposing the router is what disposes the slots it owns.
        services.AddSingleton<IStorageProvider>(_ => router);
        return services;
    }

    /// <summary>Registers a router whose slots are built from the container, for apps that configure them through
    /// the options pipeline.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="publicSlot">Builds the slot that serves the keys under <see cref="StorageKey.PublicPrefix"/>.</param>
    /// <param name="privateSlot">Builds the slot that serves every other key.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Construction is deferred to the first resolve, which would let a half-configured slot fail at the first
    /// request. To close that, the router is also resolved at host start through the same
    /// <c>ValidateOnStart</c> mechanism <see cref="Urls.StorageUrlServiceCollectionExtensions.AddThemiaStorageUrls"/> uses,
    /// so a slot whose factory throws stops the host from starting. That needs the generic host (a
    /// <c>WebApplication</c> or <c>HostBuilder</c>); without one, nothing resolves the router until first use.
    /// </remarks>
    /// <exception cref="InvalidOperationException">An <see cref="IStorageProvider"/> is already registered.</exception>
    public static IServiceCollection AddThemiaSplitStorage(
        this IServiceCollection services,
        Func<IServiceProvider, IStorageProvider> publicSlot,
        Func<IServiceProvider, IStorageProvider> privateSlot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(publicSlot);
        ArgumentNullException.ThrowIfNull(privateSlot);
        ThrowIfAProviderIsRegistered(services);

        services.AddSingleton<IStorageProvider>(sp => new SplitStorageProvider(publicSlot(sp), privateSlot(sp)));

        services.AddOptions<SplitStorageStartupCheck>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SplitStorageStartupCheck>, SplitStorageStartupValidator>());
        return services;
    }

    private static void ThrowIfAProviderIsRegistered(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IStorageProvider)))
        {
            throw new InvalidOperationException(
                "An IStorageProvider is already registered; an app has exactly one. Register either a single provider or a split, not both.");
        }
    }

    /// <summary>The empty options type whose validation resolves the router at host start.</summary>
    internal sealed class SplitStorageStartupCheck
    {
    }

    internal sealed class SplitStorageStartupValidator(IServiceProvider services) : IValidateOptions<SplitStorageStartupCheck>
    {
        public ValidateOptionsResult Validate(string? name, SplitStorageStartupCheck options)
        {
            // Resolving builds both slots and the router; a throw here propagates and stops the host.
            _ = services.GetRequiredService<IStorageProvider>();
            return ValidateOptionsResult.Success;
        }
    }
}
