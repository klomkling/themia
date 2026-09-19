using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.Storage.Urls;

/// <summary>Registers <see cref="IStorageUrlService"/>.</summary>
public static class StorageUrlServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IStorageUrlService"/> over whichever <see cref="IStorageProvider"/> is registered.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets <see cref="StorageUrlOptions.PresignedBaseUrl"/> — required with the Local provider.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>A malformed base URL fails the host at startup, naming the value, rather than on the first link minted.</remarks>
    public static IServiceCollection AddThemiaStorageUrls(
        this IServiceCollection services, Action<StorageUrlOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<StorageUrlOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        builder.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<StorageUrlOptions>, StorageUrlOptionsValidator>());

        // Singleton to match how consumers register the provider (a singleton LocalStorageProvider / S3 client).
        services.TryAddSingleton<IStorageUrlService, StorageUrlService>();
        return services;
    }

    private sealed class StorageUrlOptionsValidator : IValidateOptions<StorageUrlOptions>
    {
        public ValidateOptionsResult Validate(string? name, StorageUrlOptions options) =>
            options.Validate() is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
    }
}
