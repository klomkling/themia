using System.Net;
using Microsoft.Extensions.Options;

namespace Themia.Payments.Beam.Internal;

/// <summary>Resolves and validates <see cref="BeamOptions.BaseAddress"/>, the test-stack override of the Beam address.</summary>
internal static class BeamBaseAddress
{
    /// <summary>Returns the address to call. Validates again because <see cref="BeamOptions.BaseAddress"/> is settable after startup validation.</summary>
    public static Uri Resolve(BeamOptions options) =>
        Validate(options) is { } reason
            ? throw new InvalidOperationException(reason)
            : options.BaseAddress ?? BeamOptions.BaseAddressFor(options.Environment);

    /// <summary>Returns why the override is unusable, or <see langword="null"/> when it is absent or acceptable. Never echoes the address.</summary>
    public static string? Validate(BeamOptions options)
    {
        var address = options.BaseAddress;
        if (address is null)
            return null;
        if (options.Environment != BeamEnvironment.Playground)
            return "BeamOptions.BaseAddress can only be set when Environment is Playground; it is refused for Production.";
        if (!address.IsAbsoluteUri)
            return "BeamOptions.BaseAddress must be an absolute URI.";
        if (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
            return "BeamOptions.BaseAddress must use https (or http for a loopback host).";
        if (address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0 || address.AbsolutePath != "/")
            return "BeamOptions.BaseAddress must be a bare origin: no user info, path, query or fragment.";
        if (address.Scheme == Uri.UriSchemeHttp && !IsLoopback(address))
            return "BeamOptions.BaseAddress may use http only for a loopback host (localhost, 127.0.0.0/8, ::1); use https otherwise.";
        return null;
    }

    private static bool IsLoopback(Uri address) =>
        address.HostNameType == UriHostNameType.Dns
            ? string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            : IPAddress.TryParse(address.Host, out var ip) && IPAddress.IsLoopback(ip);
}

/// <summary>Runs <see cref="BeamBaseAddress.Validate"/> as part of options validation, so the reason reaches the startup failure.</summary>
internal sealed class BeamBaseAddressValidator : IValidateOptions<BeamOptions>
{
    public ValidateOptionsResult Validate(string? name, BeamOptions options) =>
        BeamBaseAddress.Validate(options) is { } reason ? ValidateOptionsResult.Fail(reason) : ValidateOptionsResult.Success;
}
