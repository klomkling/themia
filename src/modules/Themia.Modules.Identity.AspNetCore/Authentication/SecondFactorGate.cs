using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>What the policy decided for a user who passed the primary check.</summary>
internal enum SecondFactorGateDecision
{
    /// <summary>No second factor: the login completes.</summary>
    Proceed,

    /// <summary>A second factor is owed: <see cref="SecondFactorGateResult.Pending"/> carries the ticket to hand back instead of tokens.</summary>
    Owed,

    /// <summary>The policy refused the login: fail through the flow's normal failure path, issue nothing.</summary>
    Denied,
}

internal readonly record struct SecondFactorGateResult(SecondFactorGateDecision Decision, PendingSecondFactor? Pending)
{
    public static SecondFactorGateResult Proceed { get; } = new(SecondFactorGateDecision.Proceed, null);

    public static SecondFactorGateResult Denied { get; } = new(SecondFactorGateDecision.Denied, null);

    public static SecondFactorGateResult Owed(PendingSecondFactor pending) => new(SecondFactorGateDecision.Owed, pending);
}

/// <summary>The one place the login flows ask the policy and, when a second factor is owed, issue the ticket.</summary>
internal static class SecondFactorGate
{
    /// <summary>Asks the policy about a user who passed the primary check.</summary>
    /// <returns>Proceed, a pending login to hand back instead of tokens, or Denied.</returns>
    /// <exception cref="InvalidOperationException">The policy wants a second factor but no ticket service is registered.</exception>
    public static async Task<SecondFactorGateResult> RequireAsync(
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
        if (requirement == SecondFactorRequirement.Denied)
        {
            return SecondFactorGateResult.Denied;
        }

        var stage = requirement switch
        {
            SecondFactorRequirement.NotRequired => (SecondFactorStage?)null,
            SecondFactorRequirement.Verify => SecondFactorStage.Verify,
            SecondFactorRequirement.Enrol => SecondFactorStage.Enrol,
            _ => throw new InvalidOperationException($"The second-factor policy returned an undefined requirement ({(int)requirement})."),
        };

        if (stage is null)
        {
            return SecondFactorGateResult.Proceed;
        }

        if (tickets is null)
        {
            // Fail closed: a policy that asks for a second factor with nowhere to issue the ticket must not
            // quietly let the login through.
            throw new InvalidOperationException("The second-factor policy requires a second factor but no ISecondFactorTicketService is registered.");
        }

        var pending = await tickets.IssueAsync(
            new SecondFactorTicketData(user.Id, user.TenantId, method, provider, wasCreated, wasLinked, user.SecurityStamp, stage.Value),
            cancellationToken).ConfigureAwait(false);
        return SecondFactorGateResult.Owed(pending);
    }
}
