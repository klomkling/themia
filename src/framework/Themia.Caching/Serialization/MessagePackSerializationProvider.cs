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
            throw new InvalidOperationException(
                $"Failed to serialize type {typeof(T).FullName} via MessagePack.", ex);
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
            throw new InvalidOperationException(
                $"Failed to deserialize MessagePack payload to {typeof(T).FullName}.", ex);
        }
    }
}
