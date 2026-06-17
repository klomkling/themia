namespace Themia.Modules.Storage.Validation;

/// <summary>The result of validating an upload.</summary>
/// <param name="IsValid">Whether the upload is acceptable.</param>
/// <param name="Error">The reason it was rejected, when invalid.</param>
public readonly record struct FileValidationResult(bool IsValid, string? Error)
{
    /// <summary>A successful result.</summary>
    public static FileValidationResult Valid { get; } = new(true, null);

    /// <summary>A failed result with a reason.</summary>
    /// <param name="error">The reason.</param>
    public static FileValidationResult Invalid(string error) => new(false, error);
}

/// <summary>Validates an upload's declared content type and size before it is stored. The default
/// implementation enforces a size cap + content-type allowlist; content sniffing arrives in a future slice.</summary>
public interface IFileValidator
{
    /// <summary>Validates an upload.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="contentType">The declared content type.</param>
    /// <param name="sizeBytes">The object size in bytes.</param>
    /// <returns>The validation result.</returns>
    FileValidationResult Validate(string key, string contentType, long sizeBytes);
}
