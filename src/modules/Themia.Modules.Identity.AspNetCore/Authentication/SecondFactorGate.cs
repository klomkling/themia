using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>The one place the three login flows ask the policy and, when a second factor is owed, issue the ticket.</summary>
internal static class SecondFactorGate
{
    /// <summary>Asks the policy about a user who passed the primary check.</summary>
    /// <returns>The pending login to hand back instead of tokens, or null when the login may complete.</returns>
    /// <exception cref="InvalidOperationException">The policy wants a second factor but no ticket service is registered.</exception>
    public static async Task<PendingSecondFactor?> RequireAsync(
        ISecondFactorPolicy policy,
        ISecondFactorTicketService? tickets,
        User user,
        SecondFactorMethod method,
        string? provider,
        bool wasCreated,
        bool wasLinked,
        CancellationToken cancellationToken)
    {
        var requirement = await policy.EvaluateAsync(new SecondFactorContext(user.Id, user.TenantId, method, provider), cancellationToken).ConfigureAwait(false);
        var stage = requirement switch
        {
            SecondFactorRequirement.NotRequired => (SecondFactorStage?)null,
            SecondFactorRequirement.Verify => SecondFactorStage.Verify,
            SecondFactorRequirement.Enrol => SecondFactorStage.Enrol,
            _ => throw new InvalidOperationException($"The second-factor policy returned an undefined requirement ({(int)requirement})."),
        };

        if (stage is null)
        {
            return null;
        }

        if (tickets is null)
        {
            // Fail closed: a policy that asks for a second factor with nowhere to issue the ticket must not
            // quietly let the login through.
            throw new InvalidOperationException("The second-factor policy requires a second factor but no ISecondFactorTicketService is registered.");
        }

        return await tickets.IssueAsync(
            new SecondFactorTicketData(user.Id, user.TenantId, method, provider, wasCreated, wasLinked, user.SecurityStamp, stage.Value),
            cancellationToken).ConfigureAwait(false);
    }
}
