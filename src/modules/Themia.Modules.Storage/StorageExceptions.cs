namespace Themia.Modules.Storage;

/// <summary>Thrown when an upload fails content validation (size or content type).</summary>
public sealed class StorageValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The reason validation failed.</param>
    public StorageValidationException(string message) : base(message) { }
}

/// <summary>Thrown when an uploaded object fails a virus scan.</summary>
public sealed class StorageScanException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The threat description.</param>
    public StorageScanException(string message) : base(message) { }
}

/// <summary>Thrown when an upload would exceed the tenant's storage quota.</summary>
public sealed class StorageQuotaExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The quota details.</param>
    public StorageQuotaExceededException(string message) : base(message) { }
}
