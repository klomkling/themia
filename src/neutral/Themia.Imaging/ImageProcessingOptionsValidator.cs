using Microsoft.Extensions.Options;

namespace Themia.Imaging;

/// <summary>Fails the host at startup when <see cref="ImageProcessingOptions"/> cannot produce an image.</summary>
/// <remarks>
/// An <see cref="IValidateOptions{TOptions}"/> rather than the fluent <c>Validate(predicate, message)</c>
/// overload, whose message is fixed: the value that is wrong is the only thing worth saying.
/// </remarks>
internal sealed class ImageProcessingOptionsValidator : IValidateOptions<ImageProcessingOptions>
{
    public ValidateOptionsResult Validate(string? name, ImageProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Validate() is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
    }
}
