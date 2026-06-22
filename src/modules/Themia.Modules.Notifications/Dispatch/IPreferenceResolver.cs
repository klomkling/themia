using Themia.Notifications;

namespace Themia.Modules.Notifications.Dispatch;

/// <summary>The channels (and locale) enabled for a recipient after applying preferences.</summary>
/// <param name="EnabledChannels">The subset of requested channels that remain enabled.</param>
/// <param name="Locale">The resolved preferred locale, or null for the app default.</param>
public sealed record ResolvedPreferences(IReadOnlyList<NotificationChannel> EnabledChannels, string? Locale);

/// <summary>Resolves enabled channels and locale for a recipient from stored preferences.</summary>
public interface IPreferenceResolver
{
    /// <summary>Filters <paramref name="requested"/> to the channels enabled for <paramref name="userId"/>.</summary>
    /// <param name="userId">The recipient user identifier.</param>
    /// <param name="requested">The channels the caller wants to use.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The enabled channels and resolved locale.</returns>
    Task<ResolvedPreferences> ResolveAsync(string userId, IReadOnlyList<NotificationChannel> requested, CancellationToken ct = default);
}
