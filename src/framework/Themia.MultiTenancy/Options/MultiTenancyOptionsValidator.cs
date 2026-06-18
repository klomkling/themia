using Microsoft.Extensions.Options;

namespace Themia.MultiTenancy;

/// <summary>
/// Validates MultiTenancyOptions configuration at startup.
/// </summary>
internal sealed class MultiTenancyOptionsValidator : IValidateOptions<MultiTenancyOptions>
{
    public ValidateOptionsResult Validate(string? name, MultiTenancyOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("MultiTenancyOptions cannot be null");
        }

        if (string.IsNullOrWhiteSpace(options.HeaderName))
        {
            return ValidateOptionsResult.Fail("HeaderName cannot be null or whitespace");
        }

        // HeaderName should not contain invalid characters
        if (options.HeaderName.Any(c => char.IsControl(c) || c == ':'))
        {
            return ValidateOptionsResult.Fail("HeaderName contains invalid characters (control characters or colons are not allowed)");
        }

        // ClaimType drives the claims strategy; a blank value would silently resolve no tenant.
        if (string.IsNullOrWhiteSpace(options.ClaimType))
        {
            return ValidateOptionsResult.Fail("ClaimType cannot be null or whitespace");
        }

        return ValidateOptionsResult.Success;
    }
}
