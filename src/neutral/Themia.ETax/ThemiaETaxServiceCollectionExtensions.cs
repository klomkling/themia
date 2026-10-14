using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.ETax;
using Themia.Pdf;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI registration for Themia.ETax rendering.</summary>
public static class ThemiaETaxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TaxDocumentRenderer"/> and the Themia.Pdf services it prints with
    /// (<see cref="IVersionedPdfRenderer"/>). Idempotent.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurePdf">Optional configuration of <see cref="ThemiaPdfOptions"/>, e.g. a Chromium path.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaETaxRendering(
        this IServiceCollection services, Action<ThemiaPdfOptions>? configurePdf = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddThemiaPdf(configurePdf);
        services.TryAddSingleton<TaxDocumentRenderer>();
        return services;
    }

    /// <summary>
    /// Registers issuing and sending on top of <see cref="AddThemiaETaxRendering"/>: <see cref="TaxDocumentService"/>,
    /// <see cref="TaxDocumentSender"/> and their options. The app registers <see cref="ITaxDocumentStore"/>,
    /// <see cref="ITaxDocumentNumberSource"/> and <see cref="ITaxDocumentMailer"/>; an <see cref="ITaxDocumentFailureHandler"/> is optional
    /// (the default does nothing beyond the ERROR log). The host adds logging. Run the sender with <see cref="TaxDocumentSender.RunOnceAsync"/> on a schedule.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures <see cref="TaxDocumentSendOptions"/>; <see cref="TaxDocumentSendOptions.RevenueDepartmentAddress"/> is required.</param>
    /// <param name="configureValidation">Optional validation options (e.g. turn the tax-id checksum off during a rollout).</param>
    /// <param name="configurePdf">Optional configuration of <see cref="ThemiaPdfOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaETaxDocuments(
        this IServiceCollection services,
        Action<TaxDocumentSendOptions> configure,
        Action<TaxDocumentValidationOptions>? configureValidation = null,
        Action<ThemiaPdfOptions>? configurePdf = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var sendOptions = new TaxDocumentSendOptions();
        configure(sendOptions);
        sendOptions.Validate();
        var validation = new TaxDocumentValidationOptions();
        configureValidation?.Invoke(validation);

        services.AddThemiaETaxRendering(configurePdf);
        services.TryAddSingleton(sendOptions);
        services.TryAddSingleton(validation);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITaxDocumentFailureHandler, NullTaxDocumentFailureHandler>();
        services.TryAddSingleton<TaxDocumentService>();
        services.TryAddSingleton<TaxDocumentSender>();
        return services;
    }

    private sealed class NullTaxDocumentFailureHandler : ITaxDocumentFailureHandler
    {
        public Task OnFailedAsync(TaxDocumentFailure failure, CancellationToken ct = default) => Task.CompletedTask;
    }
}
