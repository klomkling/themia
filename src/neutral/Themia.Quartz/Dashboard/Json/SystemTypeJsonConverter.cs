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
    internal sealed class SystemTypeJsonConverter : JsonConverter<Type>
    {
        public override Type Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            var name = reader.GetString();
            if (string.IsNullOrEmpty(name))
                return null;

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
