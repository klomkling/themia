using System;

namespace Themia.AspNetCore.Exceptions;

/// <summary>A downstream dependency failed. Maps to HTTP 503.</summary>
public sealed class ExternalServiceException(
    string serviceName,
    string message,
    Exception? innerException = null)
    : ThemiaException(message, innerException: innerException)
{
    /// <summary>Name of the failing downstream service.</summary>
    public string ServiceName { get; } = serviceName;
}
