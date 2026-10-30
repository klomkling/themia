namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The default <see cref="ISecondFactorPolicy"/>: nobody owes a second factor, so every flow behaves as before.</summary>
public sealed class NotRequiredSecondFactorPolicy : ISecondFactorPolicy
{
    /// <summary>The shared instance.</summary>
    public static readonly NotRequiredSecondFactorPolicy Instance = new();

    private NotRequiredSecondFactorPolicy()
    {
    }

    /// <inheritdoc />
    public ValueTask<SecondFactorRequirement> EvaluateAsync(SecondFactorContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SecondFactorRequirement.NotRequired);
}
