using System.Text;

namespace Themia.Modules.Identity.Tokens.AspNetCore.Options;

/// <summary>JWT access-token + validation configuration. Validated at registration (fail-fast),
/// mirroring <c>IdentityModuleOptions.Validate()</c>.</summary>
public sealed class JwtOptions
{
    /// <summary>HS256 minimum key length in bytes (256-bit).</summary>
    private const int MinSigningKeyBytes = 32;

    /// <summary>The symmetric signing secret (UTF-8). Minimum 32 bytes.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>The token issuer (<c>iss</c>).</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The token audience (<c>aud</c>).</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Access-token lifetime. Default 15 minutes.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Validation clock-skew tolerance. Default 30 seconds.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Validates the options, throwing if any value would produce a broken runtime.</summary>
    /// <exception cref="ArgumentException">The signing key is too short, or issuer/audience is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A lifetime/skew is out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SigningKey) || Encoding.UTF8.GetByteCount(SigningKey) < MinSigningKeyBytes)
        {
            throw new ArgumentException(
                $"Must be at least {MinSigningKeyBytes} bytes (256-bit) for HS256.", nameof(SigningKey));
        }

        if (string.IsNullOrWhiteSpace(Issuer))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(Issuer));
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(Audience));
        }

        if (AccessTokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AccessTokenLifetime), AccessTokenLifetime, "Must be a positive duration.");
        }

        if (ClockSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ClockSkew), ClockSkew, "Must not be negative.");
        }
    }
}
