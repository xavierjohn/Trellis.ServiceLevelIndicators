# Trellis.ServiceLevelIndicators Usage Reference

Choose the correct package, wire it into a .NET application, and define the population and dimensions behind your SLIs. For a complete copyable Minimal API setup, start with the [root README](../README.md#installation).

**Source-tree availability:** result selectors, endpoint exclusions, and the instrumentation failure policy are additions after `10.0.0-preview.37`; use a package containing these changes. They are not available in preview.37. The Docker demo builds the current source.

## Package Matrix

| Scenario | Package | Purpose |
|---|---|---|
| Measure code in any .NET app | `Trellis.ServiceLevelIndicators` | Core latency SLI measurement for console apps, workers, background jobs, and shared libraries |
| Automatically measure ASP.NET Core endpoints | `Trellis.ServiceLevelIndicators.Asp` | Middleware, MVC, and Minimal API integration |
| Add API version as a metric dimension | `Trellis.ServiceLevelIndicators.Asp.ApiVersioning` | Adds `http.api.version` enrichment for apps using Asp.Versioning |

## OpenTelemetry Setup

The hosting and OTLP exporter extensions used below are separate packages, not dependencies supplied by SLI:

```shell
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

The examples assume a host/application `builder`. Import `OpenTelemetry.Metrics`, `OpenTelemetry.Resources`, and `Trellis.ServiceLevelIndicators`. Custom-meter code also needs `System.Diagnostics.Metrics`.

The OTLP exporter defaults to `http://localhost:4317`; set `OTEL_EXPORTER_OTLP_ENDPOINT` for your collector. The location examples use a plain region string and do not require Azure.Core. Use `--prerelease` when installing preview SLI packages.

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
dotnet add package Trellis.ServiceLevelIndicators --prerelease
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
Task ProcessOrderAsync(ServiceLevelIndicator sli, CancellationToken cancellationToken) =>
    sli.MeasureAsync(
        "ProcessOrder",
        () => Task.Delay(50, cancellationToken),
        new KeyValuePair<string, object?>("OrderType", "Standard"));
```

`Measure` / `MeasureAsync` infer `Success` on normal completion, `Failure` on exceptions, and `Ignored` for `OperationCanceledException`. Exceptions still propagate. Raw `StartMeasuring(...)` scopes start as `Ignored`: disposal does not inspect an exception escaping the scope. If you use raw scopes, explicitly classify normal completion, failure, and cancellation.

Direct recording is also available when you already know the elapsed time. `Record(...)` emits `CustomerResourceId`, `LocationId`, `Operation`, `Outcome`, and any custom attributes supplied to the call. Calls without an explicit outcome also default to `Ignored`.

```csharp
sli.Record("ProcessOrder", elapsedTime: 42, outcome: SliOutcome.Success);
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

These are measurement boundaries, not timeouts: they do not cancel work, change HTTP status, or determine success/failure.

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

Options are copied at construction, including thresholds; creating an internal meter does not mutate caller configuration. `sli.ServiceLevelIndicatorOptions.Meter` exposes the effective meter. Reusing options without a supplied meter creates independently owned meters. Later changes to the original options do not update an existing instance.

If you provide a custom `Meter` in `ServiceLevelIndicatorOptions`, register that same meter with OpenTelemetry.

```csharp
using var sliMeter = new Meter("MyCompany.ServiceLevelIndicator");

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

The DI container owns the singleton `ServiceLevelIndicator` and disposes any meter SLI creates at shutdown. A supplied meter remains application-owned; keep it alive until recording and export finish, then dispose it. The top-level `using` above assumes the host runs within that scope. The [console example](../sample/ConsoleApp/Program.cs) demonstrates complete meter, service-provider, and exporter lifetimes.

## ASP.NET Core MVC

Install:

```shell
dotnet add package Trellis.ServiceLevelIndicators.Asp --prerelease
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
.Enrich(context => context.AddAttribute("ProductTier", "Premium"));
```

Action-level attributes:

```csharp
[HttpPost("projects/{projectId}/sync/{mode:regex(^(quick|standard|slow)$)}")]
[ServiceLevelIndicator(Operation = "SyncProject")]
public IActionResult Sync(
    [CustomerResourceId] string projectId,
    [Measure("SyncMode")] string mode)
    => Ok();
```

Register controllers with `builder.Services.AddControllers()` and map them with `app.MapControllers()`. `AddMvc()` above installs SLI conventions; it does not replace ASP.NET Core controller registration. Automatic measurement is enabled by default. To opt in, set `AutomaticallyEmitted = false` and mark controllers/actions with `[ServiceLevelIndicator]`.

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
app.MapPost("/projects/{projectId}/sync/{mode:regex(^(quick|standard|slow)$)}",
    ([CustomerResourceId] string projectId, [Measure("SyncMode")] string mode) => Results.Ok())
    .AddServiceLevelIndicator("SyncProject");
```

The named target must be a stable, validated resource identifier. The route constraint bounds `SyncMode` to three values. See the [runnable Minimal API example](../sample/MinApi/Program.cs) for endpoint opt-in, bounded project IDs, and health exclusion.

Register `AddServiceLevelIndicator()` once per endpoint, either on its group or on the handler. Applying it to both can duplicate parameter metadata and fail endpoint initialization.

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
dotnet add package Trellis.ServiceLevelIndicators.Asp.ApiVersioning --prerelease
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

`AddApiVersion()` enriches SLI; it does not configure Asp.Versioning routing or version discovery. See the [versioned MVC example](../sample/WebApiVersioned/Program.cs) for the complete registration and query-string version reader.

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

## Enrichment and Bounded Attributes

`Enrich` receives a `WebEnrichmentContext`, not a `MeasuredOperation`. It wraps the measurement and exposes `HttpContext`, `SetCustomerResourceId(...)`, and `AddAttribute(...)`. `EnrichAsync` receives the same context plus a cancellation token and returns `ValueTask`.

For example, map a caller-tier claim to an explicitly bounded set without changing the target resource:

```csharp
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
})
.Enrich(context =>
{
    var tier = context.HttpContext.User.FindFirst("tier")?.Value switch
    {
        "free" => "free",
        "standard" => "standard",
        "premium" => "premium",
        _ => "unknown"
    };
    context.AddAttribute("CallerTier", tier);
});
```

Enrichment runs after endpoint handling and can overwrite a target already extracted from `[CustomerResourceId]`. Only call `SetCustomerResourceId` with the resolved target of the operation. A caller's `tid` claim is not necessarily that target, especially for cross-tenant administration; using it requires an explicit same-tenant guarantee. Caller identity belongs in audit logs/traces, not SLI dimensions.

`[Measure]` adds a parameter value as an attribute using the parameter name (or its configured override). Use bounded categories such as the `SyncMode` route above, never personal names or arbitrary text.

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

Callback exceptions and undefined outcomes are warning-logged and use the built-in status outcome; no lower-priority callback is tried, and enrichment continues. HTTP classifier failures do not change response status or replace the handler's completion. Explicit core result-selector exceptions still propagate.

Fallback changes the SLO population: custom-ignored `/health` may become `Success`, while a custom-client-error 429 may become `Failure`. Monitor `HttpOutcomeClassificationFailed` (EventSource event 3), including its fallback outcome, rather than relying only on warning logs.

### Cancellation-safe enrichment

Ordinary `Enrich` and `EnrichAsync` callbacks remain skipped after request cancellation. An `IEnrichment<WebEnrichmentContext>` implementation may opt in with `EnrichOnCancellation => true` only for bounded, cancellation-safe metadata access without I/O. The canceled token is still passed, and skipping an unsafe enrichment does not prevent later safe enrichments. API-version enrichment opts in automatically; known route/resource metadata is also retained.

## Instrumentation Failure Policy

Public `Record(...)`, `AddAttribute(...)`, and `SetOutcome(...)` inputs remain strictly validated. Explicit `Record(...)` provider errors also surface to its caller.

`MeasuredOperation.Dispose()` attempts recording once and reports recording errors without throwing them over the application's result or exception. Invalid legacy raw-attribute mutations drop the measurement. A failing metric provider may leave partial data; no retry or fabricated success is emitted.

In particular, histogram and threshold counts can diverge if delivery fails after the histogram observation but before all corresponding threshold increments. Reconciliation assumes successful recording; updates are not atomic across instruments.

Recording warnings use `ILogger<ServiceLevelIndicator>` when available and EventSource provider `Trellis-ServiceLevelIndicators`, event 1 (`MeasurementRecordingFailed`). DI supplies the logger when registered, while the original options-only constructor remains supported. Broken diagnostic loggers are isolated and reported by event 2 (`DiagnosticLoggingFailed`), including in middleware route-resolution, classifier, and enrichment logging.

Classifier fallback (event 3, `HttpOutcomeClassificationFailed`) and enrichment failure (event 4, `EnrichmentFailed`) are emitted even with filtered/no-op logging. Enable EventSource collection to observe them. `ServiceLevelIndicatorEventSource.Log` is the public shared contract; ASP no longer depends on core internals or exact-version lockstep.

Exception-text fields are capped at 2048 UTF-16 code units, not redacted. They may still include request data and stack traces, so apply collection/access/retention controls. `ILogger` retains the original exception and follows application logging policy.

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
7. Assuming a raw `StartMeasuring(...)` scope detects thrown work: without explicit classification it records `Ignored`, not `Failure`.
8. Replacing the target resource with the caller's tenant or emitting personal names as `[Measure]` dimensions.

## Running the Focused Examples

The [always-on Docker demo](../sample/Observability/Grafana/README.md) is the main end-to-end experience. These .NET 10 examples use the same Project Tracker vocabulary but isolate one integration each. Run commands from the repository root.

### Console: core only and custom meters

```powershell
dotnet run --project .\sample\ConsoleApp\SampleConsoleSLI.csproj
```

No Docker or collector is required. The process measures `SyncProject` for `project-blue` and exports duration and 250 ms threshold metrics to the console when the exporter is disposed. It supplies its own meter and registers that same instance with OpenTelemetry. The sample disposes the exporter, service provider, and application-owned meter.

### Web examples: reuse the observability stack

If the live demo is already running, reuse its Collector, Prometheus, and Grafana. Otherwise start only the observability services:

```powershell
docker compose -f .\sample\Observability\Grafana\docker-compose.yml up -d
```

In separate terminals, run either or both examples:

```powershell
$env:OTEL_METRIC_EXPORT_INTERVAL = "1000"
dotnet run --project .\sample\MinApi\SampleMinimalApiSli.csproj
```

```powershell
$env:OTEL_METRIC_EXPORT_INTERVAL = "1000"
dotnet run --project .\sample\WebApiVersioned\SampleVersionedWebApplicationSLI.csproj
```

Both export to `http://localhost:4317` by default and use HTTP-only loopback launch profiles; no development HTTPS certificate is needed.

| Example | Requests | What to inspect |
|---|---|---|
| Minimal API, http://localhost:5201 | `GET /projects/project-blue`; `POST /projects/project-red/sync/slow` | Named operations, project ID, bounded `SyncMode`, and a slow `Success` exceeding 250 ms |
| Versioned MVC, http://localhost:5202 | `GET /projects/project-blue?api-version=1.0` and `?api-version=2.0` | Same `GetProject` operation and project ID with distinct `http.api.version` values |

Each web sample's `Api.http` file contains ready-to-send requests. Scalar is available at http://localhost:5201/scalar/v1 and http://localhost:5202/scalar/v2 in Development.

All measured project IDs are bounded to `project-blue`, `project-green`, and `project-red`; the Minimal API also constrains sync modes. These examples deliberately opt in only their project endpoints and exclude `/health`; docs endpoints and route misses do not enter their SLO populations. The versioned sample is deterministic, not a second random-incident generator. Use the continuous demo for incidents and recovery.

The dashboard can display these metrics too, but it aggregates services with matching SLI dimensions. If you want the continuous demo's charts in isolation, stop the focused web examples with Ctrl+C. Their service names remain distinct for custom Prometheus queries. The console example exports only to the console.

## Optional API Guidance for Coding Assistants

Each SLI NuGet package carries its own versioned API reference at the package root (`trellis-api-sli*.md`) and a guidance manifest. The packages remain independent of `Trellis.Core`.

Ordinary restore/build does not write `.github/`, `AGENTS.md`, or `.agentdocs/` unless the consuming repository opts into the separate local `Trellis.AgentDocs` tool. After restoring the consuming project or solution, run once from its Git root:

```powershell
dotnet new tool-manifest --output .config
dotnet tool install Trellis.AgentDocs --version 0.1.0-preview.20 --tool-manifest .config\dotnet-tools.json
dotnet tool run agentdocs init <solution-or-project>
```

Reuse an existing root `.config\dotnet-tools.json` instead of creating another. Restoring a package never activates its guide. `init` lists packages that publish guidance as pending and prints the package IDs to add to `approvedPackages` in `.agentdocs/policy.json`. Add the SLI packages you trust, then run `dotnet tool run agentdocs sync` to install their guides into Git-root `.agentdocs/`; `init` also adds managed instruction pointers.

Guides are on-demand: the generated index lists each with a description, and an agent opens one when its task concerns SLI metrics. After a package upgrade, run `dotnet restore` and `dotnet tool run agentdocs sync`. The helper and tool are preview packages published on NuGet.org.

For maintainers, the three publishing projects use a pinned, private `Trellis.AgentDocs.Packaging` build dependency to generate manifests at pack time. Each project sets `TrellisApiRefDescription`, the one-line "Open when..." topic consumers see. Guides refer to sibling packages by ID rather than by link because every package installs into its own directory. The packaging build target is not included in published SLI packages. Run `pwsh -NoProfile -File build\test-guidance-packages.ps1` after packing to verify payloads and the absence of legacy consumer targets.

## Provider Cancellation Integration

The ASP.NET tests include real mid-flight `HttpClient` cancellation. Real `SqlClient` cancellation requires a dedicated SQL fixture; without `TRELLIS_SLI_SQL_CONNECTION_STRING`, those cases are explicitly skipped. On Windows with SQL Server Express LocalDB installed, run:

```powershell
dotnet restore Trellis.ServiceLevelIndicators.Asp\tests\Trellis.ServiceLevelIndicators.Asp.Tests.csproj
pwsh -NoProfile -File build\test-sql-cancellation.ps1
```

The script creates, starts, stops, and deletes its own uniquely named LocalDB instance. It does not use or modify shared instances. The Windows CI job runs this fixture; provider dependencies are test-only. For another dedicated SQL Server, supply the environment variable and run the ASP.NET tests; the fixture needs permission to inspect `sys.dm_exec_requests` so cancellation occurs after the command is genuinely in flight.

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
