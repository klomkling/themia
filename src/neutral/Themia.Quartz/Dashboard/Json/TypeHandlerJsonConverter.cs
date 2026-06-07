using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Themia.Quartz.Dashboard.TypeHandlers;

namespace Themia.Quartz.Dashboard.Json
{
    /// <summary>
    /// System.Text.Json replacement for the JsonSubTypes polymorphic converter previously used to
    /// (de)serialize <see cref="TypeHandlerBase"/> subtypes. Polymorphism is driven by the same runtime
    /// type map JsonSubTypes was given: the <c>TypeId</c> discriminator (= <see cref="TypeHandlerBase.TypeId"/>)
    /// maps to a concrete CLR type registered at runtime.
    /// </summary>
    /// <remarks>
    /// <para><b>Discriminator:</b> <c>TypeId</c> is a normal read-only getter on <see cref="TypeHandlerBase"/>,
    /// so STJ serializes it automatically when the concrete runtime type is written. No manual injection is
    /// needed; on read it is ignored (no setter).</para>
    /// <para><b>Recursion guard:</b> serialization/deserialization of the concrete type uses a nested
    /// <see cref="JsonSerializerOptions"/> that carries <see cref="SystemTypeJsonConverter"/> but NOT this
    /// converter, so resolving the concrete type does not re-enter here.</para>
    /// </remarks>
    internal sealed class TypeHandlerJsonConverter : JsonConverter<TypeHandlerBase>
    {
        // Caller must pass a stable snapshot — this is treated as immutable for the converter's
        // lifetime. BuildOptions copies _typesByDiscriminator before handing it in; passing the live
        // dictionary instead would race registration mutations against deserialization.
        private readonly IReadOnlyDictionary<string, Type> _typesByDiscriminator;
        private readonly JsonSerializerOptions _innerOptions;

        public TypeHandlerJsonConverter(IReadOnlyDictionary<string, Type> typesByDiscriminator, JsonSerializerOptions innerOptions)
        {
            _typesByDiscriminator = typesByDiscriminator ?? throw new ArgumentNullException(nameof(typesByDiscriminator));
            _innerOptions = innerOptions ?? throw new ArgumentNullException(nameof(innerOptions));

            // Fail fast: if innerOptions already contains a TypeHandlerJsonConverter, calling
            // Read/Write would recurse infinitely into this same converter path → StackOverflow.
            foreach (var converter in innerOptions.Converters)
            {
                if (converter is TypeHandlerJsonConverter)
                    throw new ArgumentException(
                        "innerOptions must not contain a TypeHandlerJsonConverter (would recurse).",
                        nameof(innerOptions));
            }
        }

        public override TypeHandlerBase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            // Guard non-object payloads (null/array/string/number): TryGetProperty would otherwise
            // throw InvalidOperationException, not the JsonException callers expect.
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException($"Expected a JSON object for {nameof(TypeHandlerBase)}, got {root.ValueKind}.");

            if (!root.TryGetProperty(nameof(TypeHandlerBase.TypeId), out var discriminatorElement))
                throw new UnknownTypeHandlerException($"Missing '{nameof(TypeHandlerBase.TypeId)}' discriminator for {nameof(TypeHandlerBase)}.");

            var discriminator = discriminatorElement.GetString();
            if (discriminator == null || !_typesByDiscriminator.TryGetValue(discriminator, out var concreteType))
                throw new UnknownTypeHandlerException($"Unknown {nameof(TypeHandlerBase.TypeId)} discriminator '{discriminator}'.");

            // Deserialize straight from the JsonElement (no GetRawText()+re-parse) using the inner
            // options, which carry SystemTypeJsonConverter but NOT this converter (recursion guard).
            return (TypeHandlerBase)root.Deserialize(concreteType, _innerOptions)
                ?? throw new JsonException($"Deserialization of {nameof(TypeHandlerBase.TypeId)} '{discriminator}' yielded null.");
        }

        public override void Write(Utf8JsonWriter writer, TypeHandlerBase value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            // Serialize the concrete runtime type so subclass properties (and the TypeId getter) are emitted.
            JsonSerializer.Serialize(writer, value, value.GetType(), _innerOptions);
        }
    }
}
