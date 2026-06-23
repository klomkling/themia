using System.Text;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;

namespace Themia.Modules.Identity.Tokens.AspNetCore.Signing;

/// <summary>HS256 symmetric signing provider keyed from <see cref="JwtOptions.SigningKey"/>.</summary>
public sealed class SymmetricSigningCredentialsProvider : IJwtSigningCredentialsProvider
{
    private readonly SymmetricSecurityKey key;
    private readonly SigningCredentials signingCredentials;

    /// <summary>Creates the provider from validated options.</summary>
    /// <param name="options">The JWT options carrying the signing key.</param>
    public SymmetricSigningCredentialsProvider(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    /// <inheritdoc />
    public SigningCredentials SigningCredentials => signingCredentials;

    /// <inheritdoc />
    public SecurityKey ValidationKey => key;
}
