using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Themia.Modules.Identity.Abstractions;

namespace Themia.Modules.Identity.Hashing;

/// <summary>
/// Default <see cref="IPasswordHasher"/> using argon2id. The encoded form is
/// <c>argon2id$v=19$m=&lt;memKiB&gt;,t=&lt;iterations&gt;,p=&lt;parallelism&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>,
/// which self-describes its parameters so <see cref="NeedsRehash"/> can detect outdated costs.
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const int Version = 19;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    // Current cost parameters. Raising any of these makes existing hashes "need rehash".
    private const int MemoryKiB = 19 * 1024;   // 19 MiB
    private const int Iterations = 2;
    private const int Parallelism = 1;

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, MemoryKiB, Iterations, Parallelism);

        return string.Create(CultureInfo.InvariantCulture,
            $"argon2id$v={Version}$m={MemoryKiB},t={Iterations},p={Parallelism}$" +
            $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <inheritdoc />
    public bool Verify(string encodedHash, string password)
    {
        ArgumentNullException.ThrowIfNull(encodedHash);
        ArgumentNullException.ThrowIfNull(password);

        if (!TryParse(encodedHash, out var p))
        {
            return false;
        }

        var computed = Compute(password, p.Salt, p.MemoryKiB, p.Iterations, p.Parallelism);
        return CryptographicOperations.FixedTimeEquals(computed, p.Hash);
    }

    /// <inheritdoc />
    public bool NeedsRehash(string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(encodedHash);

        if (!TryParse(encodedHash, out var p))
        {
            return true;
        }

        return p.MemoryKiB < MemoryKiB || p.Iterations < Iterations || p.Parallelism < Parallelism;
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKiB, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(HashSize);
    }

    private readonly record struct Parameters(byte[] Salt, byte[] Hash, int MemoryKiB, int Iterations, int Parallelism);

    private static bool TryParse(string encoded, out Parameters parameters)
    {
        parameters = default;

        // argon2id $ v=19 $ m=..,t=..,p=.. $ saltB64 $ hashB64
        var parts = encoded.Split('$');
        if (parts.Length != 5 || parts[0] != "argon2id")
        {
            return false;
        }

        var cost = parts[2].Split(',');
        if (cost.Length != 3)
        {
            return false;
        }

        if (!TryReadInt(cost[0], "m=", out var mem) ||
            !TryReadInt(cost[1], "t=", out var iter) ||
            !TryReadInt(cost[2], "p=", out var par))
        {
            return false;
        }

        try
        {
            parameters = new Parameters(
                Convert.FromBase64String(parts[3]),
                Convert.FromBase64String(parts[4]),
                mem, iter, par);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryReadInt(string token, string prefix, out int value)
    {
        value = 0;
        return token.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(token.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
