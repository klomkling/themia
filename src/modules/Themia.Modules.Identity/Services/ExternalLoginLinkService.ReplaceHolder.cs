using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Services;

public sealed partial class ExternalLoginLinkService
{
    /// <inheritdoc />
    public async Task<ExternalLoginLinkResult> LinkReplacingHolderAsync(
        Guid userId, ExternalIdentity identity, Guid expectedHolderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Subject);
        if (expectedHolderId == Guid.Empty)
        {
            throw new ArgumentException("The expected holder must be a user id.", nameof(expectedHolderId));
        }

        var provider = identity.Provider.ToLowerInvariant();
        var subject = identity.Subject;

        var claimant = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false);
        if (claimant is null)
        {
            return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.UserNotFound);
        }

        if (!claimant.IsActive || claimant.IsLockedOut(timeProvider.GetUtcNow()))
        {
            return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.UserInactive);
        }

        if (await ExternalLoginLookup.FindLinkAsync(links, options, provider, subject, cancellationToken)
                .ConfigureAwait(false) is not { } found)
        {
            // Nobody holds it: an ordinary link, with its own hook and race handling.
            return await LinkAsync(userId, identity, cancellationToken).ConfigureAwait(false);
        }

        var (holderLink, isPlatform) = found;
        if (holderLink.UserId == claimant.Id)
        {
            return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.AlreadyLinkedToUser);
        }

        // A holder in another partition sits where this scope cannot write: reached through the platform fallback, or
        // surfaced by a data layer that shows global rows to a tenant scope (the spec has no tenant predicate of its own).
        if (isPlatform || holderLink.TenantId != claimant.TenantId || holderLink.UserId != expectedHolderId)
        {
            return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.LinkedToAnotherUser);
        }

        var holder = await IdentityScope.ResolveUserAsync(users, expectedHolderId, cancellationToken).ConfigureAwait(false);
        if (holder is null)
        {
            return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.LinkedToAnotherUser);
        }

        // Hooks are caller code: they run before the transaction opens, never inside it.
        if (await ConsultHooksAsync(claimant, holder, provider, subject, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return refusal;
        }

        return await SwapAsync(claimant, holder, holderLink, identity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExternalLoginLinkResult?> ConsultHooksAsync(
        User claimant, User holder, string provider, string subject, CancellationToken cancellationToken)
    {
        var claimantLinks = await LinksOfAsync(claimant, provider: null, cancellationToken).ConfigureAwait(false);
        var link = await hooks
            .OnBeforeLinkExternalLoginAsync(claimant.Id, provider, subject, claimantLinks.Select(ToInfo).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        if (!link.IsAllowed)
        {
            return await RefuseAsync(claimant.Id, link, cancellationToken).ConfigureAwait(false);
        }

        var holderLinks = await LinksOfAsync(holder, provider: null, cancellationToken).ConfigureAwait(false);
        var unlink = await hooks
            .OnBeforeUnlinkExternalLoginAsync(holder.Id, provider, holderLinks.Select(ToInfo).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        return unlink.IsAllowed
            ? null
            : await RefuseAsync(holder.Id, unlink, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExternalLoginLinkResult> RefuseAsync(
        Guid refusedUserId, UserMutationDecision decision, CancellationToken cancellationToken)
    {
        await RaiseAsync(
            (o, ct) => o.OnUserMutationRefusedAsync(refusedUserId, UserMutation.ExternalLogin, decision.Reason!, ct),
            cancellationToken).ConfigureAwait(false);
        return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.Refused, decision.Reason)
        {
            RefusalCode = decision.Code,
        };
    }

    /// <summary>Removes the holder's link and inserts the claimant's in one transaction. The single-row delete
    /// by id, with its affected-row check, is the compare-and-swap; the re-read before it only skips work.</summary>
    private async Task<ExternalLoginLinkResult> SwapAsync(
        User claimant, User holder, ExternalLoginLink holderLink, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var provider = identity.Provider.ToLowerInvariant();
        var swapped = false;
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var current = await ExternalLoginLookup.FindLinkAsync(links, options, provider, identity.Subject, ct)
                    .ConfigureAwait(false);
                if (current is not { IsPlatform: false } still || still.Link.Id != holderLink.Id)
                {
                    return;
                }

                // Flushed apart so the delete precedes the insert whatever order an ORM would pick: the EF model
                // declares no unique index to order them by. The holder's link is in the ambient partition (the
                // lookup above is not the platform one), so this flush is not a platform write.
                links.Remove(still.Link);
                await IdentityScope.SaveScopedAsync(unitOfWork, filterScope, isPlatform: false, ct).ConfigureAwait(false);
                await InsertLinkAsync(claimant, provider, identity.Subject, ct).ConfigureAwait(false);
                swapped = true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UniqueConstraintException or ConcurrencyException)
        {
            // A racing claimant took the link first: our delete matched no row, or our insert hit the index.
            // The transaction rolled back, so the holder's link is as the winner left it.
            swapped = false;
        }

        if (!swapped)
        {
            return await AnswerAfterLostSwapAsync(claimant, identity, cancellationToken).ConfigureAwait(false);
        }

        await AnnounceAsync(claimant.Id, cancellationToken).ConfigureAwait(false);
        await AnnounceAsync(holder.Id, cancellationToken).ConfigureAwait(false);
        return new ExternalLoginLinkResult(ExternalLoginLinkOutcome.Linked);
    }

    private async Task<ExternalLoginLinkResult> AnswerAfterLostSwapAsync(
        User claimant, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var owner = await ExternalLoginLookup
            .FindLinkAsync(links, options, identity.Provider.ToLowerInvariant(), identity.Subject, cancellationToken)
            .ConfigureAwait(false);
        return owner switch
        {
            // Nobody holds it any more and nobody re-linked it: one ordinary attempt, never an exception.
            null => await LinkAsync(claimant.Id, identity, cancellationToken).ConfigureAwait(false),
            { } o when o.Link.UserId == claimant.Id => new ExternalLoginLinkResult(ExternalLoginLinkOutcome.AlreadyLinkedToUser),
            _ => new ExternalLoginLinkResult(ExternalLoginLinkOutcome.LinkedToAnotherUser),
        };
    }
}
