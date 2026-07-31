using Themia.Messaging.Outbox;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// Engine-specific SQL for the notifications outbox. The claim/complete/fail contract, and the drainer
/// that drives it, are the shared ones from <c>Themia.Messaging</c>; this names the notifications row
/// shape so the per-engine packages (<c>Themia.Modules.Notifications.PostgreSql</c>/<c>.MySql</c>/
/// <c>.SqlServer</c>) bind to the notifications table specifically.
/// </summary>
public interface INotificationsSqlDialect : IOutboxDialect<ClaimedOutboxRow>;
