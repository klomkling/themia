using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Themia.Totp;

/// <inheritdoc cref="ITotpService" />
public sealed class TotpService : ITotpService
{
    private readonly ITotpReplayStore _replayStore;
    private readonly TimeProvider _timeProvider;
    private readonly TotpOptions _options;

    /// <summary>Initializes a new instance of the <see cref="TotpService"/> class.</summary>
    /// <param name="replayStore">Records consumed steps. Required — see <see cref="ITotpReplayStore"/>.</param>
    /// <param name="timeProvider">
    /// Supplies the current instant. Injected rather than read from <c>DateTimeOffset.UtcNow</c> so
    /// verification — the half with the guard in it — can be pinned by a test at a fixed instant.
    /// </param>
    /// <param name="options">Code shape and verification window.</param>
    public TotpService(ITotpReplayStore replayStore, TimeProvider timeProvider, IOptions<TotpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _replayStore = replayStore ?? throw new ArgumentNullException(nameof(replayStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));

        if (_options.Digits is < 6 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.Digits, "Digits must be between 6 and 10.");
        }

        if (_options.Period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.Period, "Period must be positive.");
        }

        if (_options.VerificationWindowSteps < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.VerificationWindowSteps, "VerificationWindowSteps cannot be negative.");
        }
    }

    /// <inheritdoc />
    public string GenerateSecret(int byteLength = 20)
    {
        if (byteLength < 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteLength), byteLength, "A TOTP secret must be at least 16 bytes (RFC 4226 §4).");
        }

        return Base32.Encode(RandomNumberGenerator.GetBytes(byteLength));
    }

    /// <inheritdoc />
    public Uri CreateProvisioningUri(string secret, string issuer, string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        // The label is "issuer:account" and the issuer is ALSO a parameter: apps disagree about which
        // they read, and omitting either shows the wrong name on somebody's phone.
        //
        // Escape the two halves SEPARATELY and join with a literal colon. Escaping the joined string
        // turns the separator into %3A, and an app then reads the whole thing as one opaque account
        // label - it scans, and shows the wrong name.
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}";
        var query = new StringBuilder()
            .Append("secret=").Append(Uri.EscapeDataString(secret.Replace("=", string.Empty, StringComparison.Ordinal)))
            .Append("&issuer=").Append(Uri.EscapeDataString(issuer))
            .Append("&algorithm=").Append(_options.Algorithm.ToString().ToUpperInvariant())
            .Append("&digits=").Append(_options.Digits.ToString(CultureInfo.InvariantCulture))
            .Append("&period=").Append(((long)_options.Period.TotalSeconds).ToString(CultureInfo.InvariantCulture));

        return new Uri($"otpauth://totp/{label}?{query}");
    }

    /// <inheritdoc />
    public string GenerateCode(string secret) => ComputeCode(secret, CurrentStep());

    /// <inheritdoc />
    public async ValueTask<TotpVerification> VerifyAsync(
        string secretId,
        string secret,
        string code,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        if (string.IsNullOrWhiteSpace(code))
        {
            return new TotpVerification(TotpOutcome.InvalidCode, -1);
        }

        var current = CurrentStep();
        var window = _options.VerificationWindowSteps;

        for (var offset = -window; offset <= window; offset++)
        {
            var step = current + offset;
            if (!FixedTimeEquals(ComputeCode(secret, step), code))
            {
                continue;
            }

            // Consume the step the code MATCHED, not the current one. With a tolerance the two differ,
            // and recording the current step would admit this same code again one step later.
            var free = await _replayStore.TryConsumeAsync(secretId, step, ct).ConfigureAwait(false);

            return free
                ? new TotpVerification(TotpOutcome.Valid, step)
                : new TotpVerification(TotpOutcome.Replayed, step);
        }

        return new TotpVerification(TotpOutcome.InvalidCode, -1);
    }

    private long CurrentStep()
        => _timeProvider.GetUtcNow().ToUnixTimeSeconds() / (long)_options.Period.TotalSeconds;

    private string ComputeCode(string secret, long step)
    {
        var key = Base32.Decode(secret);

        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[64];
        var written = _options.Algorithm switch
        {
            TotpAlgorithm.Sha256 => HMACSHA256.HashData(key, counter, hash),
            TotpAlgorithm.Sha512 => HMACSHA512.HashData(key, counter, hash),
            _ => HMACSHA1.HashData(key, counter, hash),
        };

        // RFC 4226 §5.4 dynamic truncation.
        var offset = hash[written - 1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];

        var modulo = (int)Math.Pow(10, _options.Digits);
        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(_options.Digits, '0');
    }

    /// <summary>Compares in time independent of where the first difference is, so a timing signal cannot leak the code.</summary>
    private static bool FixedTimeEquals(string expected, string actual)
    {
        Span<byte> expectedBytes = stackalloc byte[32];
        Span<byte> actualBytes = stackalloc byte[32];

        if (!Encoding.UTF8.TryGetBytes(expected, expectedBytes, out var expectedLength) ||
            !Encoding.UTF8.TryGetBytes(actual.Trim(), actualBytes, out var actualLength))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            expectedBytes[..expectedLength], actualBytes[..actualLength]);
    }
}
