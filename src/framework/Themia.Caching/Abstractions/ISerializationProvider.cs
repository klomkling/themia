namespace Themia.Caching;

/// <summary>
/// Provides serialization and deserialization for cache values.
/// </summary>
public interface ISerializationProvider
{
    /// <summary>
    /// Serializes a value to a byte array.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <returns>The serialized byte array.</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <exception cref="System.InvalidOperationException">Thrown when serialization fails.</exception>
    byte[] Serialize<T>(T value);

    /// <summary>
    /// Deserializes a byte array to a value.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="data">The byte array to deserialize.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when deserialization fails.</exception>
    T? Deserialize<T>(byte[] data);
}
