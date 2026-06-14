namespace Themia.Modules.Identity.Abstractions;

/// <summary>Hashes and verifies passwords. The default implementation is argon2id; swap via DI to override.</summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a plaintext password into a self-describing encoded string.</summary>
    /// <param name="password">The plaintext password.</param>
    /// <returns>An encoded hash that embeds the algorithm parameters and salt.</returns>
    string Hash(string password);

    /// <summary>Verifies a plaintext password against an encoded hash.</summary>
    /// <param name="encodedHash">A hash previously produced by <see cref="Hash"/>.</param>
    /// <param name="password">The plaintext password to check.</param>
    /// <returns><see langword="true"/> when the password matches.</returns>
    bool Verify(string encodedHash, string password);

    /// <summary>Indicates whether an existing hash should be re-computed because the hashing parameters have changed.</summary>
    /// <param name="encodedHash">A hash previously produced by <see cref="Hash"/>.</param>
    /// <returns><see langword="true"/> when the caller should re-hash on the next successful verify.</returns>
    bool NeedsRehash(string encodedHash);
}
