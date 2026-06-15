using System.Text;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.AspNetCore.Options;

namespace Themia.Modules.Identity.AspNetCore.Signing;

/// <summary>HS256 symmetric signing provider keyed from <see cref="JwtOptions.SigningKey"/>.</summary>
public sealed class SymmetricSigningCredentialsProvider : IJwtSigningCredentialsProvider
{
    private readonly SymmetricSecurityKey key;

    /// <summary>Creates the provider from validated options.</summary>
    /// <param name="options">The JWT options carrying the signing key.</param>
    public SymmetricSigningCredentialsProvider(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
    }

    /// <inheritdoc />
    public SigningCredentials SigningCredentials => new(key, SecurityAlgorithms.HmacSha256);

    /// <inheritdoc />
    public SecurityKey ValidationKey => key;
}
