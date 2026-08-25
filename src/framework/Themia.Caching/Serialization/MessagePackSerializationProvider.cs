using MessagePack;

namespace Themia.Caching;

/// <summary>
/// MessagePack-based serialization provider using LZ4 block array compression.
/// Stateless and thread-safe implementation.
/// </summary>
public sealed class MessagePackSerializationProvider : ISerializationProvider
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);

    /// <inheritdoc />
    public bool CanHandle(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return CanHandleCore(type, []);
    }

    /// <summary>
    /// Asks the resolver the question serialization would ask, without an instance.
    /// </summary>
    /// <remarks>
    /// The generic arguments matter as much as the type itself. <c>IReadOnlyList&lt;T&gt;</c> HAS a
    /// formatter, so checking only the outer type reports a collection of contract-less records as
    /// serializable when serializing it throws — the element is what has no formatter.
    /// </remarks>
    private static bool CanHandleCore(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            // Already proven acceptable on this walk; a self-referencing generic must not loop.
            return true;
        }

        try
        {
            if (Options.Resolver.GetFormatterDynamic(type) is null)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A resolver may throw rather than return null for a type it cannot handle.
            return false;
        }

        foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
        {
            if (!CanHandleCore(argument, visited))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        try
        {
            return MessagePackSerializer.Serialize(value, Options);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new CacheSerializationException(
                $"Failed to serialize type {typeof(T).FullName} via MessagePack. MessagePack requires a "
                + "contract: types need [MessagePackObject] (or a resolver), and interface-typed members "
                + "such as IReadOnlyList<T> are not serializable. Configure UseJsonSerialization() if your "
                + "model types are plain records.",
                typeof(T),
                nameof(MessagePackSerializationProvider),
                ex);
        }
    }

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            return default;
        }

        try
        {
            return MessagePackSerializer.Deserialize<T>(data, Options);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new CacheSerializationException(
                $"Failed to deserialize MessagePack payload to {typeof(T).FullName}.",
                typeof(T),
                nameof(MessagePackSerializationProvider),
                ex);
        }
    }
}
