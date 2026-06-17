using System.Diagnostics.CodeAnalysis;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.AspNetCore.External;

/// <summary>Resolves a registered <see cref="IExternalAuthProvider"/> by its (case-insensitive) name.</summary>
public interface IExternalAuthProviderRegistry
{
    /// <summary>Attempts to resolve a provider by name (case-insensitive).</summary>
    /// <param name="name">The provider key (e.g. <c>google</c>, <c>line</c>).</param>
    /// <param name="provider">The resolved provider, if found.</param>
    /// <returns><see langword="true"/> if a provider with that name is registered.</returns>
    bool TryGet(string name, [NotNullWhen(true)] out IExternalAuthProvider? provider);
}

/// <summary>Default <see cref="IExternalAuthProviderRegistry"/> over the set of registered providers,
/// keyed by <see cref="IExternalAuthProvider.Name"/> case-insensitively. A duplicate name is a
/// registration error (fail-fast).</summary>
public sealed class ExternalAuthProviderRegistry : IExternalAuthProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IExternalAuthProvider> providers;

    /// <summary>Creates the registry from the registered providers.</summary>
    /// <param name="providers">The registered providers.</param>
    /// <exception cref="ArgumentException">Two providers share the same (case-insensitive) name.</exception>
    public ExternalAuthProviderRegistry(IEnumerable<IExternalAuthProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var map = new Dictionary<string, IExternalAuthProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (!map.TryAdd(provider.Name, provider))
            {
                throw new ArgumentException(
                    $"Duplicate external-auth provider name '{provider.Name}'.", nameof(providers));
            }
        }

        this.providers = map;
    }

    /// <inheritdoc />
    public bool TryGet(string name, [NotNullWhen(true)] out IExternalAuthProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(name);
        return providers.TryGetValue(name, out provider);
    }
}
