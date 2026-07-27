using HandlebarsDotNet;

namespace Themia.Pdf;

/// <summary>
/// Process-wide configuration for Themia PDF rendering, bound once at <c>AddThemiaPdf</c> time.
/// Controls how the headless Chromium browser is provisioned and lets consumers extend the
/// Handlebars template engine.
/// </summary>
public sealed class ThemiaPdfOptions
{
    /// <summary>
    /// Path to a Chrome/Chromium executable to launch. When set, this browser is used and no
    /// download occurs. When <see langword="null"/> (default), provisioning falls back to
    /// <see cref="DisableAutoDownload"/>.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// When <see langword="false"/> (default) and no <see cref="ExecutablePath"/> is set, Chromium is
    /// downloaded on first render. When <see langword="true"/>, auto-download is skipped and an
    /// <see cref="ExecutablePath"/> is required — otherwise the first render throws
    /// <see cref="System.InvalidOperationException"/>.
    /// </summary>
    public bool DisableAutoDownload { get; set; }

    /// <summary>Chromium launch arguments. Defaults to the sandbox/dev-shm flags ported from ezy-assets.</summary>
    public string[] LaunchArgs { get; set; } =
        ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"];

    /// <summary>Whether Chromium runs headless. Default <see langword="true"/>.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// Maximum number of renders allowed to run at once. Callers beyond the limit queue (honouring their
    /// cancellation token) rather than opening another Chromium page. Default <c>2</c>. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// This is a memory bound, not a throughput knob. Every concurrent render opens its own page in the
    /// shared browser process, and a page holds the rendered document — so unbounded concurrency means
    /// unbounded RAM, which on a small or shared host shows up as Chromium OOM-killing a neighbouring
    /// process rather than as a failed render.
    ///
    /// <para>The default is deliberately small rather than "whatever the caller happens to send". PDF
    /// rendering is normally low-rate and bursty, so a low ceiling costs little latency in exchange for a
    /// predictable memory envelope; raise it once you have measured a page's real cost on your host.</para>
    ///
    /// <para><strong>This bound is per process, not per host or per cluster.</strong> It is a
    /// <see cref="System.Threading.SemaphoreSlim"/>, so it knows nothing about other instances. Behind a load
    /// balancer, or when several applications share a host, the real ceiling is
    /// <c>instances × MaxConcurrency</c> — and because the renderer is a singleton per process, each instance
    /// also runs its <em>own</em> Chromium, so a host's worst case is
    /// <c>instances × (browser baseline + MaxConcurrency × page cost)</c>. Size accordingly.</para>
    ///
    /// <para>If the goal is "never let this starve a neighbouring process", set a container memory limit as
    /// well. No in-process bound can protect a process it does not live in. A cluster-wide render budget
    /// would need a distributed lease rather than this option.</para>
    /// </remarks>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>
    /// Optional hook to register custom helpers or partials on the Handlebars engine at construction.
    /// The built-in <c>emptyIfNull</c> helper is always registered first.
    /// </summary>
    public Action<IHandlebars>? ConfigureHandlebars { get; set; }
}
