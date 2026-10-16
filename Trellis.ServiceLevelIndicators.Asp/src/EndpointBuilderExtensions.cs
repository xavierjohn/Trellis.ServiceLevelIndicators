namespace Trellis.ServiceLevelIndicators;

using System.Reflection;
using Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for configuring SLI metadata on endpoints and route groups.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Marks a Minimal API endpoint for SLI metric emission and scans handler parameters
    /// for <see cref="CustomerResourceIdAttribute"/> and <see cref="MeasureAttribute"/>.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="operation">An optional custom operation name; if omitted, the route template is used.</param>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    public static TBuilder AddServiceLevelIndicator<TBuilder>(this TBuilder builder, string? operation = default)
        where TBuilder : notnull, IEndpointConventionBuilder
    {
        builder.WithMetadata(new ServiceLevelIndicatorAttribute() { Operation = operation });
        builder.Finally(AddSliMetadata);
        return builder;
    }

    /// <summary>
    /// Excludes an endpoint or route group from automatic SLI measurement.
    /// Exclusion takes precedence over automatic emission and explicit SLI inclusion metadata.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint or route-group builder.</param>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    public static TBuilder ExcludeFromServiceLevelIndicator<TBuilder>(this TBuilder builder)
        where TBuilder : notnull, IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new ExcludeFromServiceLevelIndicatorAttribute());
    }

    private static void AddSliMetadata(EndpointBuilder endpoint)
    {
        // this appears to be the only way to get back to the original method signature
        // REF: https://github.com/dotnet/aspnetcore/blob/main/src/Http/Routing/src/RouteEndpointDataSource.cs#L183
        if (endpoint.Metadata.OfType<MethodInfo>().FirstOrDefault()?.GetParameters() is not { } parameters)
        {
            return;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var attributes = parameter.GetCustomAttributes(inherit: false);

            for (var j = 0; j < attributes.Length; j++)
            {
                switch (attributes[j])
                {
                    case CustomerResourceIdAttribute:
                        if (endpoint.Metadata.OfType<CustomerResourceIdMetadata>().Any())
                            throw new InvalidOperationException("Multiple " + nameof(CustomerResourceIdAttribute) + " defined on endpoint '" + endpoint.DisplayName + "'.");
                        endpoint.Metadata.Add(new CustomerResourceIdMetadata(parameter.Name!));
                        break;
                    case MeasureAttribute measure:
                        endpoint.Metadata.Add(new MeasureMetadata(parameter.Name!, measure.Name));
                        break;
                }
            }
        }
    }
}
