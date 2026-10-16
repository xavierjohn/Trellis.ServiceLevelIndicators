# Trellis.ServiceLevelIndicators Usage Reference

This document is a compact reference for choosing the correct package and wiring it into a .NET application.

**Source-tree availability:** result selectors and endpoint exclusion are additions after `10.0.0-preview.37`; use a package containing these changes. They are not available in preview.37.

## Package Matrix

| Scenario | Package | Purpose |
|---|---|---|
| Measure code in any .NET app | `Trellis.ServiceLevelIndicators` | Core latency SLI measurement for console apps, workers, background jobs, and shared libraries |
| Automatically measure ASP.NET Core endpoints | `Trellis.ServiceLevelIndicators.Asp` | Middleware, MVC, and Minimal API integration |
| Add API version as a metric dimension | `Trellis.ServiceLevelIndicators.Asp.ApiVersioning` | Adds `http.api.version` enrichment for apps using Asp.Versioning |

## Metric Contract

These values are part of the library contract and should be treated as stable unless you are intentionally making a breaking change.

| Metric element | Value |
|---|---|
| Meter name | `Trellis.SLI` by default |
| Instrument name | `operation.duration` |
| Unit | milliseconds (`ms`) |
| Required tag | `CustomerResourceId` |
| Required tag | `LocationId` |
| Required tag | `Operation` |
| Required tag | `Outcome` (`Success`, `Failure`, `ClientError`, or `Ignored`) |

For ASP.NET Core, the library also emits `http.request.method` and `http.response.status.code`; an abort before response headers start omits the status. `sli.request.aborted = true` marks observed aborts. `sli.request.abort_exception` is a duration-only exception-shape diagnostic on aborts with an escaping exception. `http.api.version` is emitted when API version enrichment is enabled, including on aborted requests.

The opt-in `operation.duration.threshold.count` counter has unit `{operation}`. It leaves the duration histogram unchanged.

## Core Package

Install:

```shell
dotnet add package Trellis.ServiceLevelIndicators
```

Register with OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddServiceLevelIndicatorInstrumentation();
        metrics.AddOtlpExporter();
    });
```

Register the service:

```csharp
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.CustomerResourceId = "tenant-a";
});
```

Measure work:

```csharp
async Task ProcessOrder(ServiceLevelIndicator sli)
{
    using var op = sli.StartMeasuring("ProcessOrder");
    op.AddAttribute("OrderType", "Standard");

    await Task.Delay(50);

    op.SetOutcome(SliOutcome.Success);
}
```

Direct recording is also available when you already know the elapsed time. `Record(...)` emits `CustomerResourceId`, `LocationId`, `Operation`, `Outcome`, and any custom attributes supplied to the call. Manual measurements default to `Ignored` unless you set an outcome.

```csharp
sli.Record("ProcessOrder", elapsedTime: 42);
```

### Classify returned values

Add a synchronous selector when an action reports failure through its result:

```csharp
var validation = sli.Measure(
    "ValidateOrder",
    () => ValidateOrder(order),
    result => result.IsValid ? SliOutcome.Success : SliOutcome.ClientError);

var saved = await sli.MeasureAsync(
    "SaveOrder",
    () => SaveOrderAsync(order, cancellationToken),
    result => result.IsSuccess ? SliOutcome.Success : SliOutcome.Failure);
```

The functions and result types are application code; the core is not coupled to Trellis Results. The selector runs once after successful completion, receives null when that is the returned result, and must return a defined `SliOutcome`. The original value or object is returned unchanged. Both helpers accept trailing custom attributes and feed both duration and configured threshold metrics.

Action and selector exceptions propagate and record `Failure`; `OperationCanceledException` records `Ignored`. Undefined selector values are rejected and record `Failure`. If the action fails, the selector is not invoked. Existing overloads without a selector retain success-on-completion inference.

## Threshold Latency Objectives

Configure global boundaries during startup:

```csharp
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.CustomerResourceId = "tenant-a";
    options.LatencyThresholds.Add(TimeSpan.FromSeconds(2));
    options.LatencyThresholds.Add(TimeSpan.FromSeconds(10));
});
```

`LatencyThresholds` is a get-only collection, empty by default. Values must be distinct, positive whole milliseconds; invalid configuration fails before instrument creation. The collection is snapshotted at construction. All direct recordings, scopes, helpers, and measured HTTP requests contribute once per threshold.

| Counter element | Contract |
|---|---|
| Instrument / type / unit | `operation.duration.threshold.count` / `Counter<long>` / `{operation}` |
| Value | One increment per operation per configured threshold |
| `sli.latency.threshold_ms` | Integer milliseconds, e.g. `2000` |
| `sli.latency.result` | `within` when recorded integer milliseconds are `<=` the boundary; otherwise `exceeded` |
| Retained dimensions | Required SLI tags, API version, abort indicator, and custom dimensions when present |
| Omitted dimensions | `http.request.method`, `http.response.status.code`, `sli.request.abort_exception` |

Select **one threshold**, one scope, and one eligibility policy for both sides of a ratio. Timing is independent of `Outcome`: a fast failure emits `within`, but is not successful-and-fast. For a fixed meter/resource scope, `N` thresholds and `P` distinct projected base-tag combinations add at most `2 * N * P` series. `P` includes outcome and retained optional/custom dimensions; backend resource/instance dimensions can multiply deployment-wide series further.

### A successful-and-fast policy including slow aborts

For the same service/operation/customer/version scope and selected threshold:

```text
good = count(Outcome == Success and result == within)
eligible = count(Outcome in {Success, Failure})
         + count(Outcome == Ignored and aborted and result == exceeded)
compliance = good / eligible
```

Fast failures and slow successes are misses. Slow aborts are misses; fast aborts, other ignored events, and client errors are excluded. Equality is within. Zero good events with eligible traffic is 0; a zero eligible denominator is **no data**, not 100%. Exclude health and other out-of-scope operations before aggregation. This is query policy, not a new library default.

### Azure Monitor delta-counter query

On the Azure Monitor delta-export path, each counter's `AppMetrics.Sum` is that export interval's increment. Sum these increments across the window. Do **not** count exported rows, sum `ItemCount`, select the latest/max `Sum`, or treat `Sum` as a cumulative running total.

Apply your service/resource, location, and any API-version filters before aggregation. This example selects one operation, customer, and threshold:

```kusto
AppMetrics
| where TimeGenerated > ago(1h)
| where Name == "operation.duration.threshold.count"
| where tolong(Properties["sli.latency.threshold_ms"]) == 2000
| where tostring(Properties["Operation"]) == "GET /teams/{teamId}"
| where tostring(Properties["CustomerResourceId"]) == "team1"
| extend Outcome = tostring(Properties["Outcome"]),
         Timing = tostring(Properties["sli.latency.result"]),
         Aborted = tobool(Properties["sli.request.aborted"])
| summarize Good = sumif(Sum, Outcome == "Success" and Timing == "within"),
            Eligible = sumif(Sum,
                Outcome in ("Success", "Failure")
                or (Outcome == "Ignored" and Aborted == true and Timing == "exceeded"))
| project Good, Eligible,
          Compliance = iff(Eligible > 0, todouble(Good) / todouble(Eligible), real(null))
```

Cumulative Prometheus counter scrapes need `increase`/`rate` semantics instead of summing raw cumulative samples. The counter gives ratios at configured boundaries, not arbitrary percentiles. A histogram-export path that drops buckets cannot reconstruct percentile distributions from sum/count/min/max.

## Custom Meter Registration

If you provide a custom `Meter` in `ServiceLevelIndicatorOptions`, register that same meter with OpenTelemetry.

```csharp
var sliMeter = new Meter("MyCompany.ServiceLevelIndicator");

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddServiceLevelIndicatorInstrumentation(sliMeter);
        metrics.AddOtlpExporter();
    });

builder.Services.AddServiceLevelIndicator(options =>
{
    options.Meter = sliMeter;
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.CustomerResourceId = "tenant-a";
});
```

Available registration overloads:

```csharp
metrics.AddServiceLevelIndicatorInstrumentation();
metrics.AddServiceLevelIndicatorInstrumentation("MyCompany.ServiceLevelIndicator");
metrics.AddServiceLevelIndicatorInstrumentation(sliMeter);
```

## ASP.NET Core MVC

Install:

```shell
dotnet add package Trellis.ServiceLevelIndicators.Asp
```

Register services:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddServiceLevelIndicatorInstrumentation();
        metrics.AddOtlpExporter();
    });

builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.CustomerResourceId = "tenant-a";
})
.AddMvc();
```

Add middleware:

```csharp
app.UseServiceLevelIndicator();
```

Common MVC customization points:

```csharp
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
})
.AddMvc()
.Enrich(context =>
{
    context.SetCustomerResourceId("tenant-a");
    context.AddAttribute("ProductTier", "Premium");
});
```

Action-level attributes:

```csharp
[HttpGet("orders/{customerId}/{orderType}")]
[ServiceLevelIndicator(Operation = "GetOrder")]
public IActionResult Get(
    [CustomerResourceId] string customerId,
    [Measure(Name = "OrderType")] string orderType)
    => Ok();
```

## ASP.NET Core Minimal APIs

Register services and middleware:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddServiceLevelIndicatorInstrumentation();
        metrics.AddOtlpExporter();
    });

builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.CustomerResourceId = "tenant-a";
    options.AutomaticallyEmitted = false;
});

app.UseServiceLevelIndicator();
```

Mark each endpoint that should emit SLI data:

```csharp
app.MapGet("/orders/{customerId}/{orderType}",
    ([CustomerResourceId] string customerId, [Measure(Name = "OrderType")] string orderType) => Results.Ok())
    .AddServiceLevelIndicator("GetOrder");
```

## Exclude Endpoints from Automatic SLI Measurement

```csharp
app.MapHealthChecks("/health")
    .ExcludeFromServiceLevelIndicator();

app.MapGet("/diagnostics", () => Results.Ok())
    .ExcludeFromServiceLevelIndicator();

var internalApi = app.MapGroup("/internal")
    .ExcludeFromServiceLevelIndicator();
internalApi.MapGet("/status", () => Results.Ok());
```

For MVC:

```csharp
[HttpGet("diagnostics")]
[ExcludeFromServiceLevelIndicator]
public IActionResult Diagnostics() => Ok();
```

The attribute also works on controller classes, including inherited controllers/actions. Route-group exclusions apply to nested groups and child endpoints. Exclusion takes precedence over automatic emission and explicit SLI inclusion, regardless of registration order.

The exclusion check occurs before starting a scope, attaching the SLI feature, classification, and enrichment. No automatic SLI histogram, threshold, or measurement-related diagnostic counts are emitted, even if the handler fails or aborts. A classifier returning `Ignored` only changes a normal completion's outcome and is not equivalent to exclusion.

The endpoint still executes and other ASP.NET Core/Application Insights telemetry is unchanged. Manual core measurements are unaffected. Use `TryGetMeasuredOperation(...)` in shared code because excluded endpoints do not have an automatically attached feature.

## API Versioning Package

Install:

```shell
dotnet add package Trellis.ServiceLevelIndicators.Asp.ApiVersioning
```

Register enrichment:

```csharp
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
})
.AddMvc()
.AddApiVersion();
```

This adds the `http.api.version` metric dimension when Asp.Versioning is present. The value is the single resolved API version, `Neutral`, `Unspecified`, or an empty string when the requested version is invalid or ambiguous.

## ASP.NET Runtime Helpers

These APIs are useful inside controllers, middleware, and endpoint handlers:

```csharp
var op = HttpContext.GetMeasuredOperation();
op.CustomerResourceId = "tenant-a";
op.AddAttribute("ProductTier", "Premium");

if (HttpContext.TryGetMeasuredOperation(out var measuredOperation))
{
    measuredOperation.AddAttribute("FeatureFlag", "NewCheckout");
}
```

Use `GetMeasuredOperation()` when the route is guaranteed to emit SLI metrics. Use `TryGetMeasuredOperation()` in shared middleware or filters.

## Status Semantics

For non-HTTP code, set the outcome explicitly or use `Measure(...)` / `MeasureAsync(...)` helpers to infer it:

```csharp
op.SetOutcome(SliOutcome.Success);
```

For ASP.NET Core:

| Response outcome | `Outcome` |
|---|---|
| `2xx`, `3xx` | `Success` |
| `400`, `401`, `403`, `404`, `409`, `412`, `422` | `ClientError` |
| `429`, `5xx` | `Failure` |
| Escaping exception without an observed abort | `Failure` |
| Observed request abort, with or without an escaping exception | `Ignored` |

HTTP precedence is abort first, then escaping exception, then normal-response classification. Aborts before response headers start omit `http.response.status.code`; started responses retain the actual status. The middleware never invents 499 or an abort-related 500.

For an abort with an escaping exception, `sli.request.abort_exception` is `cancellation` for a top-level `OperationCanceledException` (including `TaskCanceledException`), otherwise `other`. Wrapped cancellations and aggregate exceptions are `other`; the diagnostic does not infer cause. It is absent without an escaping exception or without an abort, and is omitted from the threshold counter.

Abort-first availability can hide slow dependency outages if the caller disconnects before the failure arrives. Pair availability with an explicit latency objective such as the slow-abort policy above; the exception-shape diagnostic supports investigation, not reliable standalone outage detection.

### Default-aware HTTP classification

```csharp
builder.Services.AddServiceLevelIndicator(options => options.LocationId = locationId)
    .ClassifyHttpOutcome((context, defaultOutcome) =>
        context.Response.StatusCode == 429 && IsCallerQuotaRejection(context)
            ? SliOutcome.ClientError
            : defaultOutcome);
```

The application supplies `IsCallerQuotaRejection`: actor quotas may be client errors, but service-capacity rejection should remain failure. `HttpOutcomeClassifier.ClassifyStatusCode(int)` exposes the default mapper. The one-argument overload retains replacement semantics; last fluent registration wins across overloads. With direct options configuration, `ClassifyOutcomeWithDefault` takes precedence over `ClassifyOutcome`. Callbacks apply only to normal completions, not aborts or escaping exceptions.

### Cancellation-safe enrichment

Ordinary `Enrich` and `EnrichAsync` callbacks remain skipped after request cancellation. An `IEnrichment<WebEnrichmentContext>` implementation may opt in with `EnrichOnCancellation => true` only for bounded, cancellation-safe metadata access without I/O. The canceled token is still passed, and skipping an unsafe enrichment does not prevent later safe enrichments. API-version enrichment opts in automatically; known route/resource metadata is also retained.

## Cardinality Guidance

Use stable dimensions that support aggregation and alerting.

Good values:

- Tenant or subscription ID
- Region or cloud environment
- Product tier
- API version
- A bounded route category or operation type

Avoid values that can explode cardinality unless your backend is designed for them:

- Email addresses
- Request IDs
- Timestamps
- Arbitrary user input
- Random GUIDs per request

## Common Mistakes

1. Using a custom `Meter` but only registering the default meter name with OpenTelemetry.
2. Treating `CustomerResourceId` as a per-request unique ID instead of a stable service dimension.
3. Forgetting `AddMvc()` when relying on MVC conventions and attribute-based overrides.
4. Forgetting `.AddServiceLevelIndicator()` on Minimal API endpoints when `AutomaticallyEmitted` is `false`.
5. Renaming `CustomerResourceId` or `LocationId` even though downstream systems depend on those exact names.
6. Reusing reserved tags: required SLI dimensions, `activity.status.code`, HTTP method/status, `sli.request.aborted`, `sli.request.abort_exception`, `sli.latency.threshold_ms`, or `sli.latency.result`.

## Public API Cheat Sheet

Core package:

- `AddServiceLevelIndicator(Action<ServiceLevelIndicatorOptions>)`
- `AddServiceLevelIndicatorInstrumentation()`
- `AddServiceLevelIndicatorInstrumentation(string meterName)`
- `AddServiceLevelIndicatorInstrumentation(Meter meter)`
- `ServiceLevelIndicator.StartMeasuring(...)`
- `ServiceLevelIndicator.Record(...)`
- `ServiceLevelIndicator.Measure<T>(operation, action, classifyOutcome, attributes)`
- `ServiceLevelIndicator.MeasureAsync<T>(operation, action, classifyOutcome, attributes)`
- `ServiceLevelIndicatorOptions.LatencyThresholds`
- `ServiceLevelIndicator.CreateLocationId(...)`
- `ServiceLevelIndicator.CreateCustomerResourceId(...)`

ASP.NET Core package:

- `UseServiceLevelIndicator()`
- `IServiceLevelIndicatorBuilder.AddMvc()`
- `IServiceLevelIndicatorBuilder.ClassifyHttpOutcome(...)`
- `HttpOutcomeClassifier.ClassifyStatusCode(int)`
- `IServiceLevelIndicatorBuilder.Enrich(...)`
- `IServiceLevelIndicatorBuilder.EnrichAsync(...)`
- `EndpointConventionBuilder.AddServiceLevelIndicator(...)`
- `EndpointConventionBuilder.ExcludeFromServiceLevelIndicator()`
- `[ExcludeFromServiceLevelIndicator]` on MVC controllers/actions
- `HttpContext.GetMeasuredOperation()`
- `HttpContext.TryGetMeasuredOperation(...)`

API versioning package:

- `IServiceLevelIndicatorBuilder.AddApiVersion()`
