using System.Text.Json;

namespace Themia.Quartz.Dashboard.Json
{
    /// <summary>
    /// Thrown by <see cref="TypeHandlerJsonConverter"/> when a TypeHandler payload is missing the
    /// <c>TypeId</c> discriminator or names one that is not registered. A dedicated type (vs a generic
    /// <see cref="JsonException"/>) lets the dashboard map a bad client token to an empty 400 without
    /// matching on exception message text.
    /// </summary>
    internal sealed class UnknownTypeHandlerException : JsonException
    {
        public UnknownTypeHandlerException(string message) : base(message)
        {
        }
    }
}
