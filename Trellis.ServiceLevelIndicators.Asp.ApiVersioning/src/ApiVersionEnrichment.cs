namespace Trellis.ServiceLevelIndicators;

using System.Threading;
using System.Threading.Tasks;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;

public sealed class ApiVersionEnrichment
    : IEnrichment<WebEnrichmentContext>
{
    public bool EnrichOnCancellation => true;

    public ValueTask EnrichAsync(WebEnrichmentContext context, CancellationToken cancellationToken)
    {
        context.AddAttribute("http.api.version", GetApiVersion(context.HttpContext));
        return ValueTask.CompletedTask;
    }

    private static string GetApiVersion(HttpContext context)
    {
        var apiVersioningFeature = context.ApiVersioningFeature;
        var versions = apiVersioningFeature.RawRequestedApiVersions;
        if (versions.Count == 1)
            return apiVersioningFeature.RequestedApiVersion?.ToString() ?? string.Empty;
        if (versions.Count > 0)
            return string.Empty;
        if (context.GetEndpoint()?.Metadata.GetMetadata<ApiVersionMetadata>() is { IsApiVersionNeutral: true })
            return "Neutral";

        return "Unspecified";
    }
}
