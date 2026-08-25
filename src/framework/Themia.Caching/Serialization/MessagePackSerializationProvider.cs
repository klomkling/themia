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
