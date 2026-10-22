---
package: Trellis.ServiceLevelIndicators.Asp
namespaces: [Trellis.ServiceLevelIndicators]
types: [IServiceLevelIndicatorBuilder, ServiceLevelIndicatorCoreServiceCollectionExtensions, ServiceLevelIndicatorServiceCollectionExtensions, ServiceLevelIndicatorHttpOptions, HttpOutcomeClassifier, ServiceLevelIndicatorApplicationBuilderExtensions, EndpointBuilderExtensions, ServiceLevelIndicatorAttribute, ExcludeFromServiceLevelIndicatorAttribute, CustomerResourceIdAttribute, MeasureAttribute, CustomerResourceIdMetadata, MeasureMetadata, WebEnrichmentContext, IServiceLevelIndicatorFeature, HttpContextExtensions]
version: v10
last_verified: 2026-10-21
audience: [llm]
---

# Trellis.ServiceLevelIndicators.Asp — API Reference

**Package:** `Trellis.ServiceLevelIndicators.Asp`  
**Namespace:** `Trellis.ServiceLevelIndicators`  
**Purpose:** ASP.NET Core integration for `Trellis.ServiceLevelIndicators`. Provides middleware that automatically emits SLI latency metrics for eligible routed requests, endpoint exclusions, MVC and Minimal API attribute conventions for tagging the target resource and bounded route values, and an enrichment pipeline for additional service dimensions. HTTP method is emitted by default.

For API-versioning-specific enrichment, see the `Trellis.ServiceLevelIndicators.Asp.ApiVersioning` reference, when installed.

See also the `Trellis.ServiceLevelIndicators` reference, when installed.

---

## Auto-emitted dimensions (in addition to the core ones)

When the middleware is registered, every measured request emits these tags on top of the core `CustomerResourceId` / `LocationId` / `Operation` / `Outcome` dimensions:

| Tag | Source |
|---|---|
| `Operation` | HTTP method plus route template (e.g. `GET /projects/{projectId}`) — derived from `ControllerActionDescriptor.AttributeRouteInfo.Template` for MVC, or from the route pattern for Minimal APIs. Overridable via `[ServiceLevelIndicator(operation)]` or `AddServiceLevelIndicator("operation")`. |
| `Outcome` | `Success` for 2xx/3xx, `ClientError` for 400/401/403/404/409/412/422, `Failure` for 429/5xx and escaping exceptions without an abort. Observed request aborts take precedence and are `Ignored`. |
| `http.response.status.code` | `HttpContext.Response.StatusCode`; omitted on abort before headers start, otherwise retained. |
| `http.request.method` | `HttpContext.Request.Method`; emitted by default. |
| `sli.request.aborted` | `true` when the request token is canceled; absent otherwise. |
| `sli.request.abort_exception` | On abort with an escaping exception: `cancellation` for top-level `OperationCanceledException` (including `TaskCanceledException`), otherwise `other`. Absent without abort or escaping exception. |

The abort diagnostic describes exception shape, not cause: wrappers and aggregates are `other` even if cancellation-related. Abort handling does not manufacture 499 or 500. **Behavior change:** an aborted request before headers start no longer emits a phantom status 200.

Configure `ServiceLevelIndicatorOptions.LatencyThresholds` to enable the core `operation.duration.threshold.count` counter (`{operation}`). It retains required/API-version/abort/custom tags, but omits HTTP status, method, and the abort-exception diagnostic. No threshold tags are added to the duration histogram.

---

## Types

### IServiceLevelIndicatorBuilder

**Declaration**

```csharp
public interface IServiceLevelIndicatorBuilder
```

Builder returned by `AddServiceLevelIndicator(...)` to chain MVC integration, HTTP-method enrichment, and custom enrichments.

**Properties**

| Name | Type | Description |
|---|---|---|
| `Services` | `IServiceCollection` | The DI service collection where SLI services are registered. |

---

### ServiceLevelIndicatorCoreServiceCollectionExtensions

**Declaration**

```csharp
public static class ServiceLevelIndicatorCoreServiceCollectionExtensions
```

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public static IServiceLevelIndicatorBuilder AddServiceLevelIndicator(this IServiceCollection services, Action<ServiceLevelIndicatorOptions> configureOptions)` | `IServiceLevelIndicatorBuilder` | Registers the `ServiceLevelIndicator` singleton, configures `ServiceLevelIndicatorOptions`, and returns a builder for additional setup. This extension is defined by the core package and used by the ASP.NET Core integration. |

---

### ServiceLevelIndicatorServiceCollectionExtensions

**Declaration**

```csharp
public static class ServiceLevelIndicatorServiceCollectionExtensions
```

Extensions on `IServiceLevelIndicatorBuilder` for opting into MVC support and enrichments.

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public static IServiceLevelIndicatorBuilder AddMvc(this IServiceLevelIndicatorBuilder builder)` | `IServiceLevelIndicatorBuilder` | Registers the MVC convention so that `[CustomerResourceId]` and `[Measure]` parameter attributes contribute endpoint metadata. **Required** for any MVC controller that uses these attributes. |
| `public static IServiceLevelIndicatorBuilder AddHttpMethod(this IServiceLevelIndicatorBuilder builder)` | `IServiceLevelIndicatorBuilder` | No-op compatibility method; `http.request.method` is emitted by default. |
| `public static IServiceLevelIndicatorBuilder ClassifyHttpOutcome(this IServiceLevelIndicatorBuilder builder, Func<HttpContext, SliOutcome> classifier)` | `IServiceLevelIndicatorBuilder` | Configures a global HTTP outcome classifier. The returned `SliOutcome` overrides the default status-code mapping for completed requests. |
| `public static IServiceLevelIndicatorBuilder ClassifyHttpOutcome(this IServiceLevelIndicatorBuilder builder, Func<HttpContext, SliOutcome, SliOutcome> classifier)` | `IServiceLevelIndicatorBuilder` | Receives context and built-in status outcome for normal completion. Return the default to preserve a mapping. Abort and escaping-exception handling take precedence. |
| `public static IServiceLevelIndicatorBuilder Enrich(this IServiceLevelIndicatorBuilder builder, Action<WebEnrichmentContext> action)` | `IServiceLevelIndicatorBuilder` | Registers a synchronous enrichment delegate. |
| `public static IServiceLevelIndicatorBuilder EnrichAsync(this IServiceLevelIndicatorBuilder builder, Func<WebEnrichmentContext, CancellationToken, ValueTask> func)` | `IServiceLevelIndicatorBuilder` | Registers an asynchronous enrichment delegate. |

Last fluent classifier registration wins across both overloads.

---

### ServiceLevelIndicatorHttpOptions

```csharp
public sealed class ServiceLevelIndicatorHttpOptions
```

| Property | Type | Description |
|---|---|---|
| `ClassifyOutcome` | `Func<HttpContext, SliOutcome>?` | Existing replacement classifier for normal completion. |
| `ClassifyOutcomeWithDefault` | `Func<HttpContext, SliOutcome, SliOutcome>?` | Context plus default outcome. Takes precedence if both properties are set directly. Fluent overloads clear the other property. |

Neither callback runs on observed request aborts or escaping exceptions.

Callback exceptions, including callback cancellation, and undefined outcomes are warning-logged and fall back to the already-computed built-in status outcome. The lower-priority callback is not tried; enrichment continues and the response is unchanged. Explicit core result-selector errors still propagate.

Fallback changes the SLO population: custom-ignored `/health` can become `Success`, and custom-client-error 429 can become `Failure`. EventSource event 3 (`HttpOutcomeClassificationFailed`) includes `operation`, `defaultOutcome`, and `error`, even when warning logs are filtered or no-op.

### HttpOutcomeClassifier

```csharp
public static class HttpOutcomeClassifier
```

`public static SliOutcome ClassifyStatusCode(int statusCode)` exposes the built-in mapping: 200-399 success; 400/401/403/404/409/412/422 client error; 429 and codes >= 500 failure; otherwise ignored. It maps status only, not abort or exception facts.

```csharp
builder.Services.AddServiceLevelIndicator(o => o.LocationId = locationId)
    .ClassifyHttpOutcome((context, defaultOutcome) =>
        context.Response.StatusCode == 429 && IsCallerQuotaRejection(context)
            ? SliOutcome.ClientError
            : defaultOutcome);
```

`IsCallerQuotaRejection` is application-specific. Caller quotas may be client errors; service-capacity rejections should remain failures.

---

### ServiceLevelIndicatorApplicationBuilderExtensions

**Declaration**

```csharp
public static class ServiceLevelIndicatorApplicationBuilderExtensions
```

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public static IApplicationBuilder UseServiceLevelIndicator(this IApplicationBuilder app)` | `IApplicationBuilder` | Adds the `ServiceLevelIndicatorMiddleware` to the request pipeline. Place after routing and before endpoint execution so the endpoint is already resolved and request handling is measured. |

---

### EndpointBuilderExtensions

**Declaration**

```csharp
public static class EndpointBuilderExtensions
```

Endpoint and route-group integration.

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public static TBuilder AddServiceLevelIndicator<TBuilder>(this TBuilder builder, string? operation = default) where TBuilder : notnull, IEndpointConventionBuilder` | `TBuilder` | Marks a Minimal API endpoint for SLI emission. Scans the handler delegate's parameters for `[CustomerResourceId]` and `[Measure]` and projects them into endpoint metadata. When `operation` is `null`, the route template is used. |
| `public static TBuilder ExcludeFromServiceLevelIndicator<TBuilder>(this TBuilder builder) where TBuilder : notnull, IEndpointConventionBuilder` | `TBuilder` | Excludes an endpoint, health-check mapping, or route group. Returns the original builder type for chaining; throws for a null builder. |

Use `AddServiceLevelIndicator()` on the endpoint or its route group to discover `[CustomerResourceId]` and `[Measure]` handler-parameter attributes, even when `AutomaticallyEmitted` is `true`. Automatic emission alone does not project these attributes into route-value metadata.

Register SLI once for each endpoint: either on its route group or on the handler. Applying `AddServiceLevelIndicator()` to both re-scans the same parameters and can fail with duplicate customer-resource metadata.

---

### ServiceLevelIndicatorAttribute

**Declaration**

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ServiceLevelIndicatorAttribute : Attribute
```

Marks a controller or action as opting in to SLI emission. Required only when `ServiceLevelIndicatorOptions.AutomaticallyEmitted` is `false`. Also accepted as endpoint metadata by `EndpointBuilderExtensions.AddServiceLevelIndicator`.

**Properties**

| Name | Type | Description |
|---|---|---|
| `Operation` | `string?` | Optional override for the `Operation` tag. When `null`, the route template is used. |

**Constructors**

| Signature | Description |
|---|---|
| `public ServiceLevelIndicatorAttribute()` | Use the route template as the operation name. |
| `public ServiceLevelIndicatorAttribute(string operation)` | Use a custom operation name. |

---

### ExcludeFromServiceLevelIndicatorAttribute

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method,
    AllowMultiple = false, Inherited = true)]
public sealed class ExcludeFromServiceLevelIndicatorAttribute : Attribute
```

Apply to a controller or action, or use the endpoint-builder extension. Controller/action attributes and route-group metadata are inherited. Exclusion wins over automatic emission and explicit inclusion, regardless of registration order; a child inclusion cannot override a parent exclusion.

The middleware checks exclusion before resolving operation/attributes, starting a scope, attaching a feature, classification, or enrichment. Excluded requests emit no automatic duration, threshold, or measurement-related diagnostic counts, including on failure and abort paths. Their HTTP behavior and other telemetry are unchanged. Explicit core measurements are not disabled.

```csharp
app.MapHealthChecks("/health").ExcludeFromServiceLevelIndicator();
var internalApi = app.MapGroup("/internal").ExcludeFromServiceLevelIndicator();
internalApi.MapGet("/status", () => Results.Ok());
```

On MVC, use `[ExcludeFromServiceLevelIndicator]` on the action or controller.

---

### CustomerResourceIdAttribute

**Declaration**

```csharp
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class CustomerResourceIdAttribute : Attribute
```

Tag a route parameter as the source of the `CustomerResourceId` tag. The middleware reads the matching route value at request time, not model-bound query-string or body values. Only one parameter per endpoint may carry this attribute (enforced at startup for MVC and at endpoint-finalize for Minimal APIs).

---

### MeasureAttribute

**Declaration**

```csharp
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class MeasureAttribute(string? name = default) : Attribute
```

Tag a route parameter to emit its matching route value as an additional measurement attribute. Query-string and body values are not read by this attribute.

**Properties**

| Name | Type | Description |
|---|---|---|
| `Name` | `string?` | Optional attribute name; when `null`, the parameter name is used. |

---

### CustomerResourceIdMetadata

**Declaration**

```csharp
public sealed class CustomerResourceIdMetadata(string routeValueName)
```

Endpoint metadata produced by the MVC convention and the Minimal API endpoint finalizer. Identifies the route value that supplies the customer resource id.

**Properties**

| Name | Type | Description |
|---|---|---|
| `RouteValueName` | `string` | Name of the route value to read at request time. |

---

### MeasureMetadata

**Declaration**

```csharp
public sealed class MeasureMetadata(string routeValueName, string? attributeName = default)
```

Endpoint metadata for an additional measured route value.

**Properties**

| Name | Type | Description |
|---|---|---|
| `RouteValueName` | `string` | Route value to read. |
| `AttributeName` | `string` | Tag key emitted with the measurement (defaults to `RouteValueName`). |

---

### WebEnrichmentContext

**Declaration**

```csharp
public class WebEnrichmentContext : IEnrichmentContext
```

Concrete enrichment context for ASP.NET Core. Passed to every registered `IEnrichment<WebEnrichmentContext>` after the response status code is known but before the metric is recorded.

**Constructor:** `public WebEnrichmentContext(MeasuredOperation operation, HttpContext httpContext)`.

**Properties**

| Name | Type | Description |
|---|---|---|
| `Operation` | `string` | The current operation name. |
| `HttpContext` | `HttpContext` | The current request's `HttpContext`. |

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public void AddAttribute(string name, object? value)` | `void` | Append an attribute to the in-flight measurement. |
| `public void SetCustomerResourceId(string id)` | `void` | Override the customer resource id for this measurement. |

Enrichments run after endpoint handling and may overwrite the resource extracted from route metadata. Supply the resolved target, not the caller's tenant unless same-tenant semantics are guaranteed. Map custom dimensions to bounded categories; do not emit caller identities or arbitrary request values.

---

### IServiceLevelIndicatorFeature

**Declaration**

```csharp
public interface IServiceLevelIndicatorFeature
```

`HttpContext` feature exposing the current request's `MeasuredOperation`. Set by the middleware on entry and cleared on exit.

**Properties**

| Name | Type | Description |
|---|---|---|
| `MeasuredOperation` | `MeasuredOperation` | The in-flight measurement for the current request. |

---

### HttpContextExtensions

**Declaration**

```csharp
public static class HttpContextExtensions
```

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public static MeasuredOperation GetMeasuredOperation(this HttpContext context)` | `MeasuredOperation` | Returns the current measurement. Throws `InvalidOperationException` if the route is not configured to emit SLI metrics. |
| `public static bool TryGetMeasuredOperation(this HttpContext context, [MaybeNullWhen(false)] out MeasuredOperation measuredOperation)` | `bool` | Non-throwing variant. Returns `false` when no SLI feature is attached. |

---

## Middleware behavior summary

The `ServiceLevelIndicatorMiddleware` (registered by `UseServiceLevelIndicator()`):

1. Reads the resolved endpoint metadata. Skips automatic measurement for exclusion metadata, even when explicit inclusion is also present. Otherwise skips when neither `AutomaticallyEmitted == true` nor a `ServiceLevelIndicatorAttribute` is present.
2. Resolves the operation name (custom override via attribute, else route template, else `"<METHOD> <unrouted>"`).
3. Collects `MeasureMetadata` route values into measurement attributes.
4. Starts a `MeasuredOperation` and attaches an `IServiceLevelIndicatorFeature` to `HttpContext.Features`.
5. Optionally overrides the customer id from a `CustomerResourceIdMetadata`-tagged route value.
6. Invokes the next middleware. On an escaping exception without a request abort, sets status to 500 if headers have not started and status is below 500, then rethrows.
7. In `finally`, classifies abort first, then escaping exception, then normal response; adds HTTP/abort tags as described above. Normal-response classifier faults use the built-in mapping after a warning. Runs enrichments in registration order, skipping those without `EnrichOnCancellation` after request cancellation. Enrichment exceptions are caught and logged.
8. Removes the feature and disposes the `MeasuredOperation`, attempting recording once without propagating recording failures.

All middleware diagnostic logging is guarded, including route-resolution warnings. A throwing logger is reported through EventSource provider `Trellis-ServiceLevelIndicators`, event 2 (`DiagnosticLoggingFailed`), without replacing handler exceptions or stopping later enrichments. The core reports recording failures through its logger and event 1 (`MeasurementRecordingFailed`); failed disposal is terminal, not retried.

Classifier failures also emit event 3 (`HttpOutcomeClassificationFailed`); enrichment failures emit event 4 (`EnrichmentFailed`, fields `operation`, `enrichmentType`, `error`). These do not depend on logging configuration; enable EventSource collection to observe them. ASP uses the public `ServiceLevelIndicatorEventSource` and `MeasuredOperation.SetOutcome` contracts, not core internals, allowing compatible core upgrades without exact-version pins.

Exception-text fields are bounded to 2048 UTF-16 code units, not redacted, and may contain request data. Provider failures can leave histogram observations without corresponding threshold increments; reconciliation is not guaranteed after a recording failure.

Throws `InvalidOperationException` if a second instance of the middleware tries to attach an SLI feature to the same request.

`Enrich` and `EnrichAsync` delegates do not opt into cancellation. Custom `IEnrichment<WebEnrichmentContext>` implementations may set `EnrichOnCancellation => true` only for bounded, cancellation-safe metadata access without I/O. Skipped enrichments do not stop later safe ones. The API-version integration opts in, retaining resolved versions on aborted requests.

Genuine unmatched routes are not measured. Abort-first availability can miss slow dependency outages when callers disconnect before failure arrives; pair availability with a successful-and-fast latency objective that includes slow aborts. The diagnostic is not a reliable standalone outage detector.

A cancellation exception without a canceled `HttpContext.RequestAborted` token records `Failure`; it is not a request abort. The middleware assigns the HTTP outcome after endpoint handling, replacing an outcome previously set by the handler. Use `ClassifyHttpOutcome(...)` for normal-response overrides.

---

## Typical usage (MVC)

Install the SLI integration and the separate hosting/exporter dependencies:

```shell
dotnet add package Trellis.ServiceLevelIndicators.Asp --prerelease
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

Within an existing ASP.NET Core `builder`:

```csharp
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Metrics;
using Trellis.ServiceLevelIndicators;

builder.Services.AddControllers();
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddServiceLevelIndicatorInstrumentation().AddOtlpExporter());

builder.Services.AddServiceLevelIndicator(o =>
{
    o.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    o.AutomaticallyEmitted = false;
})
.AddMvc();

var app = builder.Build();
app.UseRouting();
app.UseServiceLevelIndicator();
app.MapControllers();
app.Run();
```

```csharp
[ApiController]
[Route("projects/{projectId:regex(^project-(blue|green|red)$)}")]
[ServiceLevelIndicator(Operation = "SyncProject")]
public sealed class ProjectsController : ControllerBase
{
    [HttpPost("sync/{mode:regex(^(quick|standard|slow)$)}")]
    public IActionResult Sync(
        [CustomerResourceId] string projectId,
        [Measure("SyncMode")] string mode) => Ok();
}
```

## Typical usage (Minimal API)

```csharp
app.MapPost("/projects/{projectId:regex(^project-(blue|green|red)$)}/sync/{mode:regex(^(quick|standard|slow)$)}",
    ([CustomerResourceId] string projectId, [Measure("SyncMode")] string mode) => Results.Ok())
   .AddServiceLevelIndicator("SyncProject");
```

Use the same telemetry and SLI registration, but omit controller registration and `.AddMvc()` for a Minimal API-only app. Map endpoints before `app.Run()`. With `AutomaticallyEmitted = false`, only explicitly included endpoints emit automatic SLI metrics. The example constrains both target resources and mode values instead of measuring arbitrary widget IDs.
