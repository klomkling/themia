namespace Themia.Modules.Storage.Validation;

/// <summary>The default <see cref="IFileValidator"/>: rejects objects over
/// <see cref="StorageModuleOptions.MaxObjectSizeBytes"/> or outside
/// <see cref="StorageModuleOptions.AllowedContentTypes"/> (when set).</summary>
public sealed class DefaultFileValidator : IFileValidator
{
    private readonly StorageModuleOptions options;

    /// <summary>Creates the validator.</summary>
    /// <param name="options">The module options.</param>
    public DefaultFileValidator(StorageModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    public FileValidationResult Validate(string key, string contentType, long sizeBytes, System.IO.Stream? content)
    {
        if (sizeBytes > options.MaxObjectSizeBytes)
        {
            return FileValidationResult.Invalid($"Object exceeds the maximum size of {options.MaxObjectSizeBytes} bytes.");
        }

        if (options.AllowedContentTypes is { Count: > 0 } allow &&
            !allow.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            return FileValidationResult.Invalid($"Content type '{contentType}' is not allowed.");
        }

        return FileValidationResult.Valid;
    }
}
