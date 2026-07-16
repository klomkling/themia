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

/// <summary>Thrown when a public URL is requested for an object that is not in the public container (or
/// does not exist). Deliberately an exception rather than a null/placeholder URL: a URL that looks right
/// and 403s at render time is the worst of the available failure modes.</summary>
public sealed class StorageNotPublicException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The reason no public URL exists.</param>
    public StorageNotPublicException(string message) : base(message) { }
}
