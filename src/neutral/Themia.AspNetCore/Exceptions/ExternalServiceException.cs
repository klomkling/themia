using System;
using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>A downstream dependency failed. Maps to HTTP 503.</summary>
public sealed class ExternalServiceException(
    string serviceName,
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null,
    Exception? innerException = null)
    : ThemiaException(message, errorCode, metadata, innerException)
{
    /// <summary>Name of the failing downstream service.</summary>
    public string ServiceName { get; } = serviceName;
}
