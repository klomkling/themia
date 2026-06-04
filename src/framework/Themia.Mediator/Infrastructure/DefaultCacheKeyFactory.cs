using System.Text.Json;
using System.Text.Json.Serialization;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Configuration;

namespace Themia.Mediator.Infrastructure;

/// <summary>
/// Default implementation of <see cref="ICacheKeyFactory"/> that generates cache keys
/// using JSON serialization of request properties.
/// </summary>
public sealed class DefaultCacheKeyFactory : ICacheKeyFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <inheritdoc />
    public string CreateKey<TRequest>(TRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // If request provides its own cache key, use it
        if (request is ICacheKeyProvider keyProvider)
        {
            var customKey = keyProvider.GetCacheKey();
            if (string.IsNullOrWhiteSpace(customKey))
            {
                throw new InvalidOperationException(
                    $"Custom cache key provided by {typeof(TRequest).Name} is null or whitespace.");
            }

            return customKey;
        }

        // Generate default key: TypeFullName:SerializedProperties
        var typeName = typeof(TRequest).FullName ?? typeof(TRequest).Name;
        var serialized = JsonSerializer.Serialize(request, SerializerOptions);

        return $"{typeName}:{serialized}";
    }

    /// <inheritdoc />
    public string CreateTypePrefix(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        var typeName = requestType.FullName ?? requestType.Name;
        return $"QueryType:{typeName}";
    }

    /// <inheritdoc />
    public string? CreateScopeRoot(Type requestType, MediatorCachingOptions options)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(options);

        var typeName = requestType.Name;

        // Remove known suffixes (Query, Command, Request)
        var nameWithoutSuffix = typeName;
        foreach (var suffix in options.KnownTypeSuffixes)
        {
            if (typeName.EndsWith(suffix, StringComparison.Ordinal))
            {
                nameWithoutSuffix = typeName[..^suffix.Length];
                break;
            }
        }

        // Remove known verb prefixes (Get, List, Create, Update, Delete, etc.)
        foreach (var prefix in options.KnownVerbPrefixes)
        {
            if (nameWithoutSuffix.StartsWith(prefix, StringComparison.Ordinal))
            {
                var entityName = nameWithoutSuffix[prefix.Length..];
                if (!string.IsNullOrEmpty(entityName))
                {
                    return $"Scope:{entityName}";
                }
            }
        }

        // If no pattern matched, return null (no automatic scope invalidation)
        return null;
    }
}
