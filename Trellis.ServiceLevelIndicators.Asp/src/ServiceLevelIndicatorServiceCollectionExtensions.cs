namespace Trellis.ServiceLevelIndicators;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods for the ServiceLevelIndicator middleware.
/// </summary>
public static class ServiceLevelIndicatorServiceCollectionExtensions
{
    public static IServiceLevelIndicatorBuilder AddMvc(this IServiceLevelIndicatorBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<MvcOptions>(static options => options.Conventions.Add(new ServiceLevelIndicatorConvention()));
        return builder;
    }

    public static IServiceLevelIndicatorBuilder AddHttpMethod(this IServiceLevelIndicatorBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder;
    }

    /// <summary>
    /// Replaces status-code classification for normally completed HTTP requests.
    /// The last classifier registration wins across both overloads.
    /// Callback exceptions and undefined outcomes are logged; built-in classification is used instead.
    /// </summary>
    public static IServiceLevelIndicatorBuilder ClassifyHttpOutcome(this IServiceLevelIndicatorBuilder builder, Func<HttpContext, SliOutcome> classifier)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(classifier);

        builder.Services.Configure<ServiceLevelIndicatorHttpOptions>(options =>
        {
            options.ClassifyOutcome = classifier;
            options.ClassifyOutcomeWithDefault = null;
        });
        return builder;
    }

    /// <summary>
    /// Customizes normally completed HTTP requests using the built-in status-code outcome.
    /// Abort and escaping-exception handling take precedence.
    /// The last classifier registration wins across both overloads.
    /// Callback exceptions and undefined outcomes are logged; built-in classification is used instead.
    /// </summary>
    public static IServiceLevelIndicatorBuilder ClassifyHttpOutcome(
        this IServiceLevelIndicatorBuilder builder,
        Func<HttpContext, SliOutcome, SliOutcome> classifier)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(classifier);

        builder.Services.Configure<ServiceLevelIndicatorHttpOptions>(options =>
        {
            options.ClassifyOutcomeWithDefault = classifier;
            options.ClassifyOutcome = null;
        });
        return builder;
    }

    public static IServiceLevelIndicatorBuilder Enrich(this IServiceLevelIndicatorBuilder builder, Action<WebEnrichmentContext> action)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(action);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IEnrichment<WebEnrichmentContext>>(new Enrich(action)));
        return builder;
    }

    public static IServiceLevelIndicatorBuilder EnrichAsync(this IServiceLevelIndicatorBuilder builder, Func<WebEnrichmentContext, CancellationToken, ValueTask> func)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(func);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IEnrichment<WebEnrichmentContext>>(new EnrichAsync(func)));
        return builder;
    }
}
