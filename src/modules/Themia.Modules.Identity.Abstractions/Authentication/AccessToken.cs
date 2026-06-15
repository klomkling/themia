namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>A minted access token and its absolute expiry.</summary>
/// <param name="Token">The serialized JWT.</param>
/// <param name="ExpiresAt">The token's absolute expiry.</param>
public readonly record struct AccessToken(string Token, DateTimeOffset ExpiresAt);
