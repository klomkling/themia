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

    /// <summary>
    /// Reports whether this provider could serialize <paramref name="type"/> at all, without needing an
    /// instance of it.
    /// </summary>
    /// <param name="type">The type to test.</param>
    /// <returns>
    /// <see langword="false"/> only when the provider knows it would reject the type on every attempt.
    /// </returns>
    /// <remarks>
    /// Intended for startup diagnostics: a response type the serializer cannot handle makes caching a
    /// permanent no-op for that request, and this lets that be reported before a request reaches it.
    /// <para>
    /// The default implementation returns <see langword="true"/> — a provider that cannot answer must
    /// not produce a false alarm. Serialization failures are still reported at runtime when they happen,
    /// so answering "yes" here costs a diagnostic, not correctness.
    /// </para>
    /// </remarks>
    bool CanHandle(System.Type type) => true;
}
