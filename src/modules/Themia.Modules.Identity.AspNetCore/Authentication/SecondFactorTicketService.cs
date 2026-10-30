using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>
/// Default <see cref="ISecondFactorTicketService"/>. A ticket is a protected (encrypted and authenticated) payload
/// that names the user, tenant, primary method, stage and security stamp, plus a random nonce; the nonce is stored
/// hashed as a <see cref="TokenPurpose.SecondFactorTicket"/> user token, which is what makes the ticket single-use
/// and expiring. The payload cannot be altered or forged without the data-protection keys, so a ticket proves that
/// the primary check really passed. Keys must be persisted in production (as for any data-protection use); losing
/// them only invalidates tickets still in flight.
/// </summary>
internal sealed class SecondFactorTicketService : ISecondFactorTicketService
{
    /// <summary>The purpose string of the data protector. The version suffix changes if the payload format does.</summary>
    internal const string ProtectorPurpose = "Themia.Modules.Identity.SecondFactorTicket.v1";

    /// <summary>How long a ticket for a user who must verify an existing factor is accepted.</summary>
    internal static readonly TimeSpan VerifyLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a ticket for a user who must enrol a factor first is accepted: installing an authenticator takes longer.</summary>
    internal static readonly TimeSpan EnrolLifetime = TimeSpan.FromMinutes(15);

    private readonly IDataProtector protector;
    private readonly IServiceProvider services;
    private readonly TimeProvider timeProvider;

    // The user-token store is resolved when a ticket is issued or redeemed, not at construction, so the login flows
    // (which take this service) stay constructible in a container that has no persistence registered.
    public SecondFactorTicketService(IDataProtectionProvider dataProtection, IServiceProvider services, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(timeProvider);
        protector = dataProtection.CreateProtector(ProtectorPurpose);
        this.services = services;
        this.timeProvider = timeProvider;
    }

    public async Task<PendingSecondFactor> IssueAsync(SecondFactorTicketData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        var lifetime = data.Stage == SecondFactorStage.Enrol ? EnrolLifetime : VerifyLifetime;
        string nonce;
        using (TenantContextAccessor.BeginScope(data.TenantId))
        {
            nonce = await services.GetRequiredService<IUserTokenService>().GenerateAsync(data.UserId, TokenPurpose.SecondFactorTicket, lifetime, cancellationToken).ConfigureAwait(false);
        }

        var expiresAt = timeProvider.GetUtcNow().Add(lifetime);
        var payload = new Payload(
            data.UserId, data.TenantId?.Value, data.Method, data.Provider, data.WasCreated, data.WasLinked, data.SecurityStamp, data.Stage,
            nonce, expiresAt.ToUnixTimeSeconds());
        var ticket = protector.Protect(JsonSerializer.Serialize(payload));
        return new PendingSecondFactor(data.UserId, data.Stage, ticket, expiresAt);
    }

    public async Task<SecondFactorTicketData?> ConsumeAsync(string ticket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(ticket));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }

        if (payload is null || string.IsNullOrEmpty(payload.Nonce) || payload.ExpiresAtUnix < timeProvider.GetUtcNow().ToUnixTimeSeconds())
        {
            return null;
        }

        var tenant = TenantId.From(payload.Tenant);
        TokenConsumeResult consumed;
        using (TenantContextAccessor.BeginScope(tenant))
        {
            consumed = await services.GetRequiredService<IUserTokenService>().ConsumeAsync(payload.UserId, TokenPurpose.SecondFactorTicket, payload.Nonce, cancellationToken).ConfigureAwait(false);
        }

        return consumed == TokenConsumeResult.Success
            ? new SecondFactorTicketData(payload.UserId, tenant, payload.Method, payload.Provider, payload.WasCreated, payload.WasLinked, payload.Stamp, payload.Stage)
            : null;
    }

    private sealed record Payload(
        Guid UserId,
        string? Tenant,
        SecondFactorMethod Method,
        string? Provider,
        bool WasCreated,
        bool WasLinked,
        string Stamp,
        SecondFactorStage Stage,
        string Nonce,
        long ExpiresAtUnix);
}
