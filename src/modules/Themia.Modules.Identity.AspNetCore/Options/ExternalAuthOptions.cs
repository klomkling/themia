namespace Themia.Modules.Identity.AspNetCore.Options;

/// <summary>External-auth (OAuth/OIDC) provider credentials, validated at startup (fail-fast),
/// mirroring <see cref="JwtOptions.Validate"/>.</summary>
public sealed class ExternalAuthOptions
{
    /// <summary>Google OAuth credentials. Configured only when <c>AddGoogle</c> is called.</summary>
    public GoogleOptions Google { get; } = new();

    /// <summary>LINE Login credentials. Configured only when <c>AddLine</c> is called.</summary>
    public LineOptions Line { get; } = new();
}

/// <summary>Google OAuth credentials.</summary>
public sealed class GoogleOptions
{
    /// <summary>The Google OAuth client id (also the expected id_token <c>aud</c>).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The Google OAuth client secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The requested scopes. Defaults to <c>openid email profile</c>.</summary>
    public IReadOnlyList<string> Scopes { get; set; } = ["openid", "email", "profile"];

    /// <summary>Validates the options, throwing on blank credentials.</summary>
    /// <exception cref="ArgumentException">The client id or secret is blank.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(ClientId));
        }

        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(ClientSecret));
        }
    }
}

/// <summary>LINE Login credentials.</summary>
public sealed class LineOptions
{
    /// <summary>The LINE channel id (also the expected id_token <c>aud</c>).</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The LINE channel secret (the HS256 id_token signing key).</summary>
    public string ChannelSecret { get; set; } = string.Empty;

    /// <summary>The requested scopes. Defaults to <c>openid profile email</c>.</summary>
    public IReadOnlyList<string> Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>Validates the options, throwing on blank credentials.</summary>
    /// <exception cref="ArgumentException">The channel id or secret is blank.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ChannelId))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(ChannelId));
        }

        if (string.IsNullOrWhiteSpace(ChannelSecret))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(ChannelSecret));
        }
    }
}
