using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="ISessionProofStore"/> over the <c>identity.session_second_factor</c> table. Every method first asks
/// <see cref="IRefreshTokenService.IsSessionLiveAsync"/>, which resolves the owner in scope and checks the session is live for that
/// user, so a foreign, revoked or expired session reads as no proof and takes no write.</summary>
public sealed class SessionProofStore : ISessionProofStore
{
    private readonly IRepository<SessionSecondFactor, Guid> proofs;
    private readonly IRefreshTokenService refreshTokens;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the store.</summary>
    public SessionProofStore(
        IRepository<SessionSecondFactor, Guid> proofs,
        IRefreshTokenService refreshTokens,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(proofs);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.proofs = proofs;
        this.refreshTokens = refreshTokens;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<SessionProofSnapshot?> GetAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!await refreshTokens.IsSessionLiveAsync(userId, sessionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var row = await proofs.FirstOrDefaultAsync(new SessionSecondFactorOfSpec(userId, sessionId), cancellationToken).ConfigureAwait(false);
        return row is null ? null : new SessionProofSnapshot(row.Amr, row.Strength, row.AuthenticatedAt, row.Restriction);
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(Guid userId, Guid sessionId, SessionProofSnapshot proof, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (!await refreshTokens.IsSessionLiveAsync(userId, sessionId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var existing = await proofs.FirstOrDefaultAsync(new SessionSecondFactorOfSpec(userId, sessionId), cancellationToken).ConfigureAwait(false);
        var row = existing ?? new SessionSecondFactor { Id = sessionId, UserId = userId, CreatedAt = now };
        row.Amr = proof.Amr;
        row.Strength = proof.Strength;
        row.AuthenticatedAt = proof.AuthenticatedAt;
        row.Restriction = proof.Restriction;
        row.UpdatedAt = now;
        if (existing is null)
        {
            await proofs.AddAsync(row, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            proofs.Update(row);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> LiftRestrictionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!await refreshTokens.IsSessionLiveAsync(userId, sessionId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var lifted = await proofs.UpdateWhereAsync(
            new RestrictedSessionSecondFactorOfSpec(userId, sessionId),
            set => set.Set(s => s.Restriction, SessionRestriction.None).Set(s => s.UpdatedAt, now),
            cancellationToken).ConfigureAwait(false);
        return lifted > 0;
    }
}
