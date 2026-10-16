# Trellis.ServiceLevelIndicators

[![NuGet Package](https://img.shields.io/nuget/v/Trellis.ServiceLevelIndicators.svg)](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators)

A .NET library for emitting **Service Level Indicator (SLI)** latency metrics in milliseconds via the standard [System.Diagnostics.Metrics](https://learn.microsoft.com/dotnet/api/system.diagnostics.metrics) API. Use it to measure operation duration across any .NET application and attach dimensions such as operation name, customer, location, and outcome so the data is useful for SLO dashboards and alerts.

For ASP.NET Core applications, see [Trellis.ServiceLevelIndicators.Asp](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp).

## When To Use This Package

Choose `Trellis.ServiceLevelIndicators` when you want to measure operations directly in application code, especially in console apps, worker services, background jobs, libraries, or shared business logic that should stay independent from ASP.NET Core.

If you want automatic measurement of ASP.NET Core endpoints, use [Trellis.ServiceLevelIndicators.Asp](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp) instead.

## Installation

```shell
dotnet add package Trellis.ServiceLevelIndicators --prerelease
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

The OpenTelemetry hosting and OTLP exporter extensions are separate dependencies used by the examples below. Import `OpenTelemetry.Metrics` and `Trellis.ServiceLevelIndicators`; custom meters also need `System.Diagnostics.Metrics`.

## Quick Start

### 1. Register with OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddServiceLevelIndicatorInstrumentation();
        metrics.AddOtlpExporter();
    });
```

If you supply a custom `Meter` in `ServiceLevelIndicatorOptions`, register that same meter with OpenTelemetry:

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
    options.CustomerResourceId = "my-customer";
});
```

### 2. Configure options

```csharp
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.CustomerResourceId = "my-customer";
});
```

### 3. Measure operations

Prefer `Measure` / `MeasureAsync` to infer outcomes from completion, exceptions, and cancellation:

```csharp
Task DoWorkAsync(ServiceLevelIndicator sli, CancellationToken cancellationToken) =>
    sli.MeasureAsync("ProcessOrder", () => Task.Delay(50, cancellationToken));
```

These helpers record `Success` on completion, `Failure` on exceptions, and `Ignored` for `OperationCanceledException`; exceptions still propagate. Raw `StartMeasuring(...)` scopes default to `Ignored` and do not detect thrown work. If you use one, explicitly classify every completion, failure, and cancellation path.

Helpers also accept custom attributes:

```csharp
var attribute = new KeyValuePair<string, object?>("Event", "OrderCreated");
await sli.MeasureAsync("ProcessOrder", () => Task.Delay(50), attribute);
```

Custom attribute names must be unique and must not reuse SLI-reserved tags: `CustomerResourceId`, `LocationId`, `Operation`, `Outcome`, `activity.status.code`, `http.request.method`, `http.response.status.code`, `sli.request.aborted`, `sli.request.abort_exception`, `sli.latency.threshold_ms`, or `sli.latency.result`.

### Classify returned values

Use a synchronous result selector when an operation returns a failure value instead of throwing:

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

The functions and result types are application-defined. The core accepts any result type, including value types and nullable results, without a Trellis Results dependency. The selector runs once after the action completes successfully, and the helper returns the original value or object.

Selector execution is included in the measured duration and threshold comparisons, so keep classification cheap and synchronous.

Both overloads accept the same trailing custom attributes as the existing helpers. Action and selector exceptions propagate and record `Failure`; `OperationCanceledException` records `Ignored`. An undefined selector outcome is rejected and records `Failure`. If the action throws, the selector is not invoked. Existing overloads without a selector still infer `Success` on normal completion.

## Emitted Metrics

By default, a meter named `Trellis.SLI` emits the `operation.duration` histogram in milliseconds. If you configure `ServiceLevelIndicatorOptions.Meter`, metrics are emitted from that meter instead.

`StartMeasuring(...)` emits the following attributes when the returned `MeasuredOperation` is disposed:

| Attribute | Description |
|-----------|-------------|
| `Operation` | The name of the measured operation |
| `CustomerResourceId` | A **stable** identifier for the entity the operation is acting on (tenant, subscription, account, work item, etc.). NOT the caller, NOT a per-request ID, NOT a user/email. |
| `LocationId` | Where the service is running (e.g. `ms-loc://az/public/westus3`) |
| `Outcome` | `Success`, `Failure`, `ClientError`, or `Ignored` |

Direct `Record(...)` calls emit `CustomerResourceId`, `LocationId`, `Operation`, `Outcome`, and any custom attributes supplied to the call. Without an explicit outcome, manual/background measurements default to `Ignored`.

## Optional Threshold Counter

When a backend discards histogram buckets, opt in to exact timing counts at chosen thresholds:

```csharp
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.CustomerResourceId = "tenant-a";
    options.LatencyThresholds.Add(TimeSpan.FromSeconds(2));
    options.LatencyThresholds.Add(TimeSpan.FromSeconds(10));
});
```

`LatencyThresholds` is an initially empty, get-only collection. Configure distinct, positive, whole-millisecond values at startup. Invalid or duplicate values fail before instruments are created; configuration is snapshotted when the singleton is constructed.

Thresholds classify recorded elapsed time; they do not enforce timeouts, cancel work, or decide the SLI outcome.

| Element | Contract |
|---|---|
| Instrument | `operation.duration.threshold.count` |
| Type / unit | `Counter<long>` / `{operation}` |
| Value | One increment per operation per configured threshold |
| `sli.latency.threshold_ms` | Integer milliseconds, e.g. `2000` |
| `sli.latency.result` | `within` when recorded elapsed milliseconds are `<=` the threshold, otherwise `exceeded` |

All `Record`, scope, and helper paths contribute, regardless of outcome. Both instruments use the configured meter. The duration histogram keeps its original dimensions; threshold attributes exist only on the counter. The counter retains required/custom dimensions, `http.api.version`, and `sli.request.aborted` when present, but omits `http.response.status.code`, `http.request.method`, and `sli.request.abort_exception`.

Select one threshold when computing a ratio. Timing is independent of outcome: successful-and-fast objectives require `Outcome == Success` in the numerator and an explicit eligibility policy. No eligible traffic is no data, not 100%. This counter supports configured boundaries, not arbitrary percentiles.

On the Azure Monitor delta-export path, each `AppMetrics.Sum` is the interval's increment: sum those values over the window, not exported rows or `ItemCount`. Cumulative Prometheus counters instead require `increase`/`rate` semantics. See the repository usage reference for complete query recipes.

## Cardinality Guidance

Required tags must be stable and meaningful. Good values: tenant, subscription, environment, region, product tier, work-item type. Bad values: per-request GUIDs, timestamps, free-form user input, or raw emails when a stable object ID is available. The same rule applies to any custom attributes you add via `MeasuredOperation.AddAttribute(...)`.

For a fixed meter/resource scope, `N` thresholds and `P` projected base-tag combinations add at most `2 * N * P` counter series. `P` includes outcome and retained optional/custom dimensions. Backend resource/instance dimensions can further multiply deployment-wide series.

## Disposal

`ServiceLevelIndicator` is a sealed `IDisposable` and is meant to be registered as a singleton — the DI container disposes it (and the `Meter` it created) at host shutdown, so applications never need to call `Dispose()` manually. If you supply your own `Meter` via `ServiceLevelIndicatorOptions.Meter`, you own its lifetime; SLI will not dispose it.

Configuration is copied at construction, including the threshold list. Caller options are not modified, so reusing a configuration without a custom meter creates independently owned meters. `sli.ServiceLevelIndicatorOptions` exposes the instance's effective options and meter. Later changes to the original configuration do not update an existing instance.

### Recording failure policy

`Record(...)`, `AddAttribute(...)`, and `SetOutcome(...)` reject invalid inputs directly; undefined outcomes are rejected before recording side effects. Action and result-selector exceptions still propagate with their established failure/cancellation semantics.

`MeasuredOperation.Dispose()` attempts recording once, becomes terminal before emission, and does not throw recording failures over the application's result or exception. Invalid legacy `Attributes` list mutations drop the measurement and report a diagnostic. A metric-provider failure can leave partially emitted data; disposal never retries or fabricates a successful measurement.

Histogram and threshold-counter totals can therefore diverge after a recording failure: a histogram observation may be delivered before a corresponding threshold increment fails. Reconciliation assumes successful recording, not atomic updates across instruments.

Recording failures produce a warning through `ILogger<ServiceLevelIndicator>` when available and EventSource provider `Trellis-ServiceLevelIndicators` (event 1, `MeasurementRecordingFailed`). The existing constructor still works without logging services; an overload accepts `ILogger<ServiceLevelIndicator>`, and DI selects it when logging is registered. A throwing logger is isolated and reported through the same EventSource (event 2, `DiagnosticLoggingFailed`).

The shared provider is the public `ServiceLevelIndicatorEventSource.Log` contract, not an assembly-friend dependency. HTTP classifier fallback (event 3, `HttpOutcomeClassificationFailed`) and enrichment errors (event 4, `EnrichmentFailed`) are observable independently of `ILogger` filtering when an EventSource collector/listener is enabled. Classifier events include the built-in outcome used instead.

Each exception-text field is truncated to at most 2048 UTF-16 code units, including its truncation marker, without splitting surrogate pairs. This bounds payload size, not sensitive data: messages and stack traces can still contain request data. Apply appropriate collection/access/retention controls; `ILogger` receives the original exception under the application's logging policy.

## Key APIs

| Type / Method | Description |
|---------------|-------------|
| `ServiceLevelIndicator.StartMeasuring(operation, attributes)` | Start a measured operation scope |
| `ServiceLevelIndicator.Measure<T>(operation, action, classifyOutcome, attributes)` | Measure a synchronous action, classify its result, and return that same result |
| `ServiceLevelIndicator.MeasureAsync<T>(operation, action, classifyOutcome, attributes)` | Await a `Task<T>`, classify its result synchronously, and return that same result |
| `MeasuredOperation.SetOutcome(outcome)` | Set the SLI outcome |
| `MeasuredOperation.AddAttribute(name, value)` | Add a custom metric attribute |
| `MeasuredOperation.CustomerResourceId` | Get/set the customer resource ID |
| `ServiceLevelIndicator.CreateLocationId(cloud, region?, zone?)` | Helper to build a location ID string |
| `ServiceLevelIndicator.CreateCustomerResourceId(guid)` | Helper to build a customer resource ID from a service tree GUID |
| `ServiceLevelIndicatorEventSource.Log` | Public, shared instrumentation-failure diagnostic provider |

## Optional AgentDocs setup

This package includes a versioned API reference, but restoring it does not install
agent instructions. To opt in, restore your consuming project or solution, then
run from its Git root:

```powershell
dotnet new tool-manifest --output .config
dotnet tool install Trellis.AgentDocs --version 0.1.0-preview.20 --tool-manifest .config/dotnet-tools.json
dotnet tool run agentdocs init <solution-or-project>
```

If the repository already has `.config/dotnet-tools.json`, reuse it instead of
creating another manifest. Restoring a package never activates its guide: `init`
lists `Trellis.ServiceLevelIndicators` as pending and prints the package IDs to add to
`approvedPackages` in `.agentdocs/policy.json`. Add the ones you trust, then run
`dotnet tool run agentdocs sync` to install the guides under Git-root
`.agentdocs/`. The guide is on-demand: the generated index describes it, and an
agent opens it when its task concerns SLI metrics. After a package upgrade, run
`dotnet restore` and then `dotnet tool run agentdocs sync`.

## Further Reading

- [Full documentation and samples](https://github.com/xavierjohn/Trellis.ServiceLevelIndicators)
- [Package selection and usage reference](https://github.com/xavierjohn/Trellis.ServiceLevelIndicators/blob/main/docs/usage-reference.md)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/dotnet/)
