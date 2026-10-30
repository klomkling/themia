using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Challenges;

namespace Themia.Conversations.Internal;

/// <summary>Fails the host at start when guest access is registered without the challenge purpose it needs, instead of at the first invite.</summary>
internal sealed class GuestConfigurationValidator(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var challengeOptions = services.GetService<ChallengeOptions>()
            ?? throw new InvalidOperationException("Guest access needs Themia.Challenges: call AddThemiaChallenges(...) and one engine registration.");

        // GetPurpose throws when the purpose was never configured.
        var purpose = challengeOptions.GetPurpose(ConversationGuestOptions.ChallengePurpose);
        if (purpose.Format.Kind != ChallengeFormatKind.OpaqueToken || purpose.MaxLiveChallenges != 1)
        {
            throw new InvalidOperationException(
                $"The '{ConversationGuestOptions.ChallengePurpose}' challenge purpose must be an opaque token with one live challenge; configure it with ConfigureConversationGuestPurpose.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
