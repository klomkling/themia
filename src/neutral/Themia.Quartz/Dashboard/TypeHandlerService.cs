using HandlebarsDotNet;
using Themia.Quartz.Dashboard.Json;
using Themia.Quartz.Dashboard.TypeHandlers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Themia.Quartz.Dashboard
{
    public class TypeHandlerService
    {
        readonly Dictionary<Type, TypeHandlerDescriptor> _handlers = new Dictionary<Type, TypeHandlerDescriptor>();

        readonly Services _services;

        // Discriminator (TypeId) -> concrete CLR type, mirroring JsonSubTypes' runtime registration.
        readonly Dictionary<string, Type> _typesByDiscriminator = new Dictionary<string, Type>();

        public DateTime LastModified { get; private set; }

        JsonSerializerOptions _jsonSerializerOptions = null;
        readonly object _optionsLock = new object();
        private JsonSerializerOptions JsonSerializerOptions
        {
            get
            {
                if (_jsonSerializerOptions == null)
                {
                    lock (_optionsLock)
                    {
                        if (_jsonSerializerOptions == null)
                            _jsonSerializerOptions = BuildOptions();
                    }
                }

                return _jsonSerializerOptions;
            }
        }

        // Inner options resolve concrete handler types (and System.Type via AQN) WITHOUT the polymorphic
        // converter, so the polymorphic converter does not re-enter itself. The outer options add the
        // polymorphic converter on top for the TypeHandlerBase entry point.
        JsonSerializerOptions BuildOptions()
        {
            var inner = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = null, // PascalCase
            };
            inner.Converters.Add(new SystemTypeJsonConverter());

            var map = new Dictionary<string, Type>(_typesByDiscriminator);

            var outer = new JsonSerializerOptions(inner);
            outer.Converters.Add(new TypeHandlerJsonConverter(map, inner));
            return outer;
        }

        class TypeHandlerDescriptor
        {
            public Type Type { get; set; }

            public string TypeId { get; set; }

            public HandlebarsTemplate<object, string> Render { get; set; }

            public TypeHandlerResourcesAttribute Resources { get; set; }
        }

        public TypeHandlerService(Services services)
        {
            _services = services;

            if (services?.Options?.StandardTypes != null)
            {
                foreach (var typeHandler in services.Options.StandardTypes.Select(x => x.GetType()).Distinct())
                    Register(typeHandler);
            }

            Register(typeof(UnsupportedTypeHandler));
        }

        // Registration is expected to complete at startup before the first Serialize/Deserialize call.
        // The discriminator map (_typesByDiscriminator) and options reset are not guarded by a lock,
        // so concurrent registration after first use is not safe.
        public void Register(Type type)
        {
            if (!typeof(TypeHandlerBase).IsAssignableFrom(type))
                throw new ArgumentException("Type must inherit from " + nameof(TypeHandlerBase));

            var desc = new TypeHandlerDescriptor()
            {
                Type = type,
                TypeId = TypeHandlerBase.GetTypeId(type),
                Resources = TypeHandlerResourcesAttribute.GetResolved(type),
            };

            desc.Render = _services.Handlebars.Compile(desc.Resources.Template);

            _handlers.Add(type, desc);

            _typesByDiscriminator[desc.TypeId] = type;

            _jsonSerializerOptions = null; // reset cached json options

            LastModified = DateTime.UtcNow;
        }

        // A top-level JSON "null" bypasses the polymorphic converter (STJ returns null for null tokens),
        // so guard here too — callers (e.g. ChangeType) dereference the result.
        public TypeHandlerBase Deserialize(string str) =>
            JsonSerializer.Deserialize<TypeHandlerBase>(Encoding.UTF8.GetString(Convert.FromBase64String(str)), JsonSerializerOptions)
                ?? throw new JsonException("Deserialized TypeHandler payload was null.");

        public string Serialize(TypeHandlerBase typeHandler) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(typeHandler, JsonSerializerOptions)));

        public string Render(TypeHandlerBase typeHandler, object model)
        {
            if (_handlers.TryGetValue(typeHandler.GetType(), out var desc))
                return desc.Render(model);
            else
                throw new InvalidOperationException("Type handler not registered: " + typeHandler.GetType().FullName);
        }

        public Dictionary<string, string> GetScripts()
        {
            return _handlers.Values
                .Select(x => new { x.TypeId, x.Resources.Script })
                .ToArray()
                .Where(x => !string.IsNullOrWhiteSpace(x.Script))
                .ToDictionary(x => x.TypeId, x => x.Script);
        }
    }
}
