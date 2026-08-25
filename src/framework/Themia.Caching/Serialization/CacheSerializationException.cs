namespace Themia.Caching;

/// <summary>
/// Thrown when the configured <see cref="ISerializationProvider"/> cannot serialize or deserialize a
/// type at all.
/// </summary>
/// <remarks>
/// This is a <b>permanent</b> failure, not a transient one: the configured serializer will reject the
/// same type on every subsequent call, so a caller that swallows it disables caching for that type for
/// the lifetime of the process. Callers that treat cache faults as recoverable should still swallow it,
/// but should report it differently from a connection or timeout fault.
/// <para>
/// Derives from <see cref="InvalidOperationException"/>, which is what the serialization providers threw
/// before this type existed, so existing <c>catch (InvalidOperationException)</c> handlers are unaffected.
/// </para>
/// </remarks>
public sealed class CacheSerializationException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="CacheSerializationException"/> class.</summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="serializedType">The type the serializer rejected.</param>
    /// <param name="serializerName">Name of the serialization provider that rejected it.</param>
    /// <param name="innerException">The underlying serializer exception.</param>
    public CacheSerializationException(
        string message,
        Type serializedType,
        string serializerName,
        Exception? innerException)
        : base(message, innerException)
    {
        SerializedType = serializedType;
        SerializerName = serializerName;
    }

    /// <summary>Gets the type the configured serializer could not handle.</summary>
    public Type SerializedType { get; }

    /// <summary>Gets the name of the serialization provider that rejected the type.</summary>
    public string SerializerName { get; }
}
