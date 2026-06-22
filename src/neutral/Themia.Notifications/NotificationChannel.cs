namespace Themia.Notifications;

/// <summary>The delivery channel for a notification.</summary>
public enum NotificationChannel
{
    /// <summary>Email.</summary>
    Email = 0,
    /// <summary>SMS / text message.</summary>
    Sms = 1,
    /// <summary>In-app notification record.</summary>
    InApp = 2,
    /// <summary>Mobile/web push (provider seam; no built-in provider).</summary>
    Push = 3,
}
