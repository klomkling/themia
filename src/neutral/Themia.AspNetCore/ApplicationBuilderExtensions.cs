using Microsoft.AspNetCore.Builder;

namespace Themia.AspNetCore;

/// <summary>Registration helpers for Themia's ProblemDetails middleware.</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>Adds <see cref="ProblemDetailsMiddleware"/> to the pipeline. Place it early, before MVC/endpoints.</summary>
    public static IApplicationBuilder UseThemiaProblemDetails(this IApplicationBuilder app)
        => app.UseMiddleware<ProblemDetailsMiddleware>();
}
