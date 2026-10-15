---
package: Trellis.ServiceLevelIndicators.Asp.ApiVersioning
namespaces: [Trellis.ServiceLevelIndicators]
types: [ServiceLevelIndicatorServiceCollectionExtensions, ApiVersionEnrichment]
version: v10
last_verified: 2026-10-15
audience: [llm]
---

# Trellis.ServiceLevelIndicators.Asp.ApiVersioning — API Reference

**Package:** `Trellis.ServiceLevelIndicators.Asp.ApiVersioning`  
**Namespace:** `Trellis.ServiceLevelIndicators`  
**Purpose:** Adds the resolved API version as the `http.api.version` measurement dimension to every request emitted by `Trellis.ServiceLevelIndicators.Asp`. Use only when your ASP.NET Core application also uses the [`Asp.Versioning`](https://github.com/dotnet/aspnet-api-versioning) package family. This package is an extension; it does **not** replace `Trellis.ServiceLevelIndicators.Asp`.

See also the `Trellis.ServiceLevelIndicators` and `Trellis.ServiceLevelIndicators.Asp` references, when installed.

---

## Emitted dimension

| Tag | Value |
|---|---|
| `http.api.version` | The single resolved API version (e.g. `2023-06-06`), `"Neutral"` if the endpoint is API-version-neutral, `"Unspecified"` if no version was requested and no default was assumed, or `""` if the request is invalid or ambiguous. |

Resolved versions are retained on aborted MVC and Minimal API requests, and on the opt-in `operation.duration.threshold.count` counter. Date-based values remain date-only strings; Kusto may interpret date-like dynamic properties as `datetime`, which should be normalized in queries rather than by changing library formatting.

---

## Types

### ServiceLevelIndicatorServiceCollectionExtensions

**Declaration**

```csharp
public static class ServiceLevelIndicatorServiceCollectionExtensions
```

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public static IServiceLevelIndicatorBuilder AddApiVersion(this IServiceLevelIndicatorBuilder builder)` | `IServiceLevelIndicatorBuilder` | Registers `ApiVersionEnrichment` so every measured request emits an `http.api.version` tag. Chain this onto the builder returned by `AddServiceLevelIndicator(...)`. |

---

### ApiVersionEnrichment

**Declaration**

```csharp
public sealed class ApiVersionEnrichment : IEnrichment<WebEnrichmentContext>
```

Reads `HttpContext.ApiVersioningFeature` and emits `http.api.version`. Resolution rules:

| `RawRequestedApiVersions.Count` | Endpoint metadata | Emitted value |
|---|---|---|
| `1` | — | `RequestedApiVersion?.ToString()` (e.g. `"2023-06-06"`) |
| `> 1` | — | `""` (ambiguous) |
| `0` | `ApiVersionMetadata.IsApiVersionNeutral == true` | `"Neutral"` |
| `0` | otherwise | `"Unspecified"` |

This enrichment is registered as a singleton via `TryAddEnumerable`, so calling `AddApiVersion()` more than once is idempotent.

**Properties**

| Name | Type | Value | Description |
|---|---|---|---|
| `EnrichOnCancellation` | `bool` | `true` | Runs after request cancellation because version resolution is bounded metadata access without I/O. User async enrichments do not opt in automatically. |

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public ValueTask EnrichAsync(WebEnrichmentContext context, CancellationToken cancellationToken)` | `ValueTask` | Implements `IEnrichment<WebEnrichmentContext>.EnrichAsync`. Adds `http.api.version` to the in-flight `MeasuredOperation`. |

---

## Typical usage

```csharp
using Asp.Versioning;
using Trellis.ServiceLevelIndicators;

builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(2023, 6, 6);
    o.ReportApiVersions = true;
}).AddMvc();

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddServiceLevelIndicatorInstrumentation().AddOtlpExporter());

builder.Services.AddServiceLevelIndicator(o =>
{
    o.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
})
.AddMvc()
.AddApiVersion();   // ← from Trellis.ServiceLevelIndicators.Asp.ApiVersioning

var app = builder.Build();
app.UseRouting();
app.UseServiceLevelIndicator();
```

A request to a `[ApiVersion("2023-06-06")]` endpoint then emits the standard SLI tags plus `http.api.version=2023-06-06`. Requests to an `[ApiVersionNeutral]` endpoint emit `http.api.version=Neutral`.
