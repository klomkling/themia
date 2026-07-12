using CronExpressionDescriptor;
using Microsoft.AspNetCore.Http;
using Quartz;
using Themia.Quartz.Dashboard;
using Themia.Quartz.Dashboard.TypeHandlers;
using Number = Themia.Quartz.Dashboard.TypeHandlers.NumberHandler.UnderlyingType;

namespace Themia.Quartz;

/// <summary>
/// Configuration for the Themia Quartz dashboard. Ported from SilkierQuartz's options minus the
/// dropped cookie-authentication settings; authorization is now a host-supplied <see cref="Authorize"/>
/// delegate.
/// </summary>
public sealed class ThemiaQuartzOptions
{
    /// <summary>
    /// Logo image source. Supports any value valid as an <c>img src</c> attribute (URL or base64
    /// data URI). Defaults to the bundled dashboard logo.
    /// </summary>
    public string Logo { get; set; } = "Content/Images/logo.png";

    /// <summary>Path to an additional stylesheet injected into the dashboard layout.</summary>
    public string CustomStyleSheet { get; set; } = "";

    /// <summary>Path to a custom favicon used by the dashboard layout.</summary>
    public string CustomFavicon { get; set; } = "";

    /// <summary>Product name shown in the dashboard header and page title. Defaults to "Themia Scheduler".</summary>
    public string ProductName { get; set; } = "Themia Scheduler";

    /// <summary>Raw HTML emitted verbatim at the end of the dashboard layout's <c>&lt;head&gt;</c> — after the
    /// built-in CSS and <see cref="CustomStyleSheet"/>, so it can override both. Use it for chrome the
    /// stylesheet hooks cannot express: an external script, extra links or meta tags. Empty (the default)
    /// emits nothing.
    /// <para><strong>Not encoded</strong> — this is a trusted, adopter-authored slot; never build it from
    /// user input.</para>
    /// <para>A relative URL in this markup resolves against the layout's <c>&lt;base&gt;</c> (the dashboard
    /// root, <see cref="VirtualPathRoot"/>), not the host app root — use a root-relative or absolute URL to
    /// reference host-app assets. (The exceptions dashboard's slots differ: that page has no
    /// <c>&lt;base&gt;</c>.)</para></summary>
    public string HeadHtml { get; set; } = "";

    /// <summary>Raw HTML emitted verbatim immediately after <c>&lt;body&gt;</c> opens, before the dashboard's
    /// own navigation menu. Use it for a header bar, a back-link to the host app, or a theme toggle. Empty
    /// (the default) emits nothing. <strong>Not encoded</strong> — same trust and relative-URL rules as
    /// <see cref="HeadHtml"/>.</summary>
    public string BodyStartHtml { get; set; } = "";

    /// <summary>The virtual path the dashboard is mounted under. Defaults to <c>/jobs</c>.</summary>
    public string VirtualPathRoot { get; set; } = "/jobs";

    /// <summary>The application base path the dashboard is hosted within. Defaults to <c>/</c>.</summary>
    public string BasePath { get; set; } = "/";

    /// <summary>The URL-encoded form of <see cref="VirtualPathRoot"/>.</summary>
    public string VirtualPathRootUrlEncode => VirtualPathRoot.Replace("/", "%2F");

    /// <summary>The Quartz scheduler the dashboard manages.</summary>
    public IScheduler? Scheduler { get; set; }

    /// <summary>Supported value types for the job data map editor.</summary>
    public List<TypeHandlerBase> StandardTypes { get; } = new List<TypeHandlerBase>();

    /// <summary>The type pre-selected when adding a new job data map item.</summary>
    public TypeHandlerBase DefaultSelectedType { get; set; }

    /// <summary>The default date format used when rendering and parsing dates in the dashboard.</summary>
    public string DefaultDateFormat
    {
        get => DateTimeSettings.DefaultDateFormat;
        set => DateTimeSettings.DefaultDateFormat = value;
    }

    /// <summary>The default time format used when rendering and parsing times in the dashboard.</summary>
    public string DefaultTimeFormat
    {
        get => DateTimeSettings.DefaultTimeFormat;
        set => DateTimeSettings.DefaultTimeFormat = value;
    }

    /// <summary>Whether the dashboard renders times in local time (otherwise UTC).</summary>
    public bool UseLocalTime
    {
        get => DateTimeSettings.UseLocalTime;
        set => DateTimeSettings.UseLocalTime = value;
    }

    /// <summary>Options passed to the cron-expression description provider.</summary>
    public Options CronExpressionOptions { get; set; } = new Options { DayOfWeekStartIndexZero = false };

    /// <summary>Whether the dashboard permits editing (create/update/delete) of scheduler objects.</summary>
    public bool EnableEdit { get; set; } = true;

    /// <summary>
    /// Host-supplied authorization gate for every dashboard request; returns <see langword="true"/> to allow.
    /// When <see langword="null"/> the dashboard is DENY-ALL (the host MUST supply this to grant access).
    /// </summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }

    /// <summary>
    /// HTTP status returned when the <see cref="Authorize"/> gate denies a dashboard request. Default
    /// <c>404</c> (Not Found), which doesn't reveal the route to an unauthenticated probe and matches the
    /// Themia.Exceptional dashboard. Set to <c>403</c> if you prefer an explicit Forbidden.
    /// </summary>
    public int DeniedStatusCode { get; set; } = StatusCodes.Status404NotFound;

    internal string? ContentRootDirectory => null;

    internal string? ViewsRootDirectory => null;

    /// <summary>Initializes a new instance with the default supported type handlers registered.</summary>
    public ThemiaQuartzOptions()
    {
        DefaultSelectedType = new StringHandler() { Name = "String" };
        // order of StandardTypes is important due to TypeHandlerBase.CanHandle evaluation
        StandardTypes.Add(new FileHandler() { Name = "File", DisplayName = "Binary Data" });
        StandardTypes.Add(new BooleanHandler() { Name = "Boolean" });
        StandardTypes.Add(new DateTimeHandler() { Name = "Date", DisplayName = "Date", IgnoreTimeComponent = true });
        StandardTypes.Add(new DateTimeHandler() { Name = "DateTime" });
        StandardTypes.Add(new DateTimeHandler() { Name = "DateTimeUtc", DisplayName = "DateTime (UTC)", IsUtc = true });
        StandardTypes.Add(new NumberHandler(Number.Decimal));
        StandardTypes.Add(new NumberHandler(Number.Double));
        StandardTypes.Add(new NumberHandler(Number.Float));
        StandardTypes.Add(new NumberHandler(Number.Integer));
        StandardTypes.Add(new NumberHandler(Number.Long));
        StandardTypes.Add(DefaultSelectedType); // String
        StandardTypes.Add(new StringHandler() { Name = "MultilineString", DisplayName = "String (Multiline)", IsMultiline = true });
    }
}
