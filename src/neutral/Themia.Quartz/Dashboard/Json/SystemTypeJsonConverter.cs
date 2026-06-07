using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Themia.Quartz.Dashboard.Json
{
    /// <summary>
    /// Serializes <see cref="Type"/> as its <see cref="Type.AssemblyQualifiedName"/> string, matching
    /// the Newtonsoft.Json wire format that the dashboard previously produced (e.g. EnumHandler.EnumType).
    /// System.Text.Json has no built-in Type converter, so this reproduces the round-trip:
    /// write AQN string, read back via <see cref="Type.GetType(string, bool)"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Read behavior:</b> a normal round-trip never yields a null/empty token — a null
    /// <see cref="Type"/> is omitted by <c>WhenWritingNull</c>, so the property is simply absent and
    /// this converter is not invoked. A null/empty token therefore only arises from a malformed or
    /// tampered payload; we fail fast with a <see cref="JsonException"/> rather than return <c>null</c>,
    /// which callers (e.g. <c>EnumHandler.EnumType</c>) dereference and would otherwise hit a
    /// <see cref="System.NullReferenceException"/>. A present, non-empty string that cannot be resolved
    /// throws via <see cref="Type.GetType(string, bool)"/> with <c>throwOnError: true</c>.</para>
    /// </remarks>
    internal sealed class SystemTypeJsonConverter : JsonConverter<Type>
    {
        // Opt in to null handling: STJ bypasses reference-type converters for a JSON null token by
        // default, which would let an "EnumType": null payload deserialize to a null Type and make the
        // null-token fail-fast below dead code. With this, Read is invoked for null and throws.
        public override bool HandleNull => true;

        public override Type Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                throw new JsonException($"Expected an assembly-qualified type name for {nameof(Type)}, got a JSON null.");

            var name = reader.GetString();
            if (string.IsNullOrEmpty(name))
                throw new JsonException($"Expected a non-empty assembly-qualified type name for {nameof(Type)}.");

            return Type.GetType(name, throwOnError: true);
        }

        public override void Write(Utf8JsonWriter writer, Type value, JsonSerializerOptions options)
        {
            if (value == null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value.AssemblyQualifiedName);
        }
    }
}
