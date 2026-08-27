using Microsoft.Extensions.Options;

namespace Themia.Totp;

/// <summary>Fails the host at startup when <see cref="TotpOptions"/> cannot produce usable codes.</summary>
/// <remarks>
/// An <see cref="IValidateOptions{TOptions}"/> rather than the fluent <c>Validate(predicate, message)</c>
/// overload, whose message is fixed: the value that is wrong is the only thing worth saying, and a boot
/// failure reading "Invalid TotpOptions" sends whoever is on call back to the source.
/// </remarks>
internal sealed class TotpOptionsValidator : IValidateOptions<TotpOptions>
{
    public ValidateOptionsResult Validate(string? name, TotpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Validate() is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
    }
}
