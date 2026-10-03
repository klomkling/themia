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
    /// <para>
    /// <b>You dispose the slots.</b> The router is registered as an instance, so no container disposes it or the
    /// slots, and every container built from this collection shares them: disposing one — a temporary
    /// <c>BuildServiceProvider()</c> during start-up, say — must not take them from the real host. Slots normally
    /// live for the process. Pass the slots by name; the router cannot tell a swapped pair from a right one.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">An <see cref="IStorageProvider"/> is already registered.</exception>
    public static IServiceCollection AddThemiaSplitStorage(
        this IServiceCollection services, IStorageProvider publicSlot, IStorageProvider privateSlot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ThrowIfAProviderIsRegistered(services);

        services.AddSingleton<IStorageProvider>(new SplitStorageProvider(publicSlot, privateSlot, ownsSlots: false));
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
    /// <c>WebApplication</c> or <c>HostBuilder</c>); without one, nothing resolves the router until first use. The
    /// start-up check also fails if another <see cref="IStorageProvider"/> was registered after this call, which
    /// would otherwise silently replace the router.
    /// <para>
    /// <b>The router never disposes these slots.</b> Return slots the container already manages (resolve them from
    /// the <see cref="IServiceProvider"/> the factory receives), so it disposes each once. A slot built with
    /// <c>new</c> inside a factory is yours to dispose, and one built before a later factory throws is not cleaned up.
    /// </para>
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

        services.AddSingleton<IStorageProvider>(sp => new SplitStorageProvider(publicSlot(sp), privateSlot(sp), ownsSlots: false));

        services.AddOptions<SplitStorageStartupCheck>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SplitStorageStartupCheck>, SplitStorageStartupValidator>());
        return services;
    }

    private static void ThrowIfAProviderIsRegistered(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IStorageProvider) && !d.IsKeyedService))
        {
            throw new InvalidOperationException(
                "An IStorageProvider is already registered; an app has exactly one. Register either a single provider or a split, not both.");
        }
    }

    /// <summary>The empty options type whose validation resolves the router at host start.</summary>
    internal sealed class SplitStorageStartupCheck
    {
    }

    internal sealed class SplitStorageStartupValidator(IStorageProvider provider) : IValidateOptions<SplitStorageStartupCheck>
    {
        public ValidateOptionsResult Validate(string? name, SplitStorageStartupCheck options)
        {
            // The container builds the router to construct this validator, so a slot that cannot be built has
            // already thrown by here. What is left to catch is a provider registered later that replaced it.
            return provider is SplitStorageProvider
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    $"AddThemiaSplitStorage was used, but IStorageProvider resolves to {provider.GetType().Name}: a later registration replaced the split.");
        }
    }
}
