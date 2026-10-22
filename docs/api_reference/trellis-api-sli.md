---
package: Trellis.ServiceLevelIndicators
namespaces: [Trellis.ServiceLevelIndicators]
types: [ServiceLevelIndicator, ServiceLevelIndicatorOptions, SliOutcome, MeasuredOperation, ServiceLevelIndicatorEventSource, IServiceLevelIndicatorBuilder, ServiceLevelIndicatorCoreServiceCollectionExtensions, IEnrichmentContext, "IEnrichment<T>", ServiceLevelIndicatorMeterProviderBuilderExtensions]
version: v10
last_verified: 2026-10-21
audience: [llm]
---

# Trellis.ServiceLevelIndicators — API Reference

**Package:** `Trellis.ServiceLevelIndicators`  
**Namespace:** `Trellis.ServiceLevelIndicators`  
**Purpose:** Core library for emitting Service Level Indicator (SLI) latency metrics in milliseconds via `System.Diagnostics.Metrics` / OpenTelemetry. Use directly in console apps, worker services, background jobs, and shared libraries that should stay independent from ASP.NET Core. For ASP.NET Core integration, see the `Trellis.ServiceLevelIndicators.Asp` reference, when installed.

See also the `Trellis.ServiceLevelIndicators.Asp` and `Trellis.ServiceLevelIndicators.Asp.ApiVersioning` references, when installed.

---

## Default emitted dimensions

`StartMeasuring(...)` scopes emit the following tags on instrument `operation.duration` (ms, `Histogram<long>`) when the returned `MeasuredOperation` is disposed:

| Tag | Source |
|---|---|
| `CustomerResourceId` | `ServiceLevelIndicatorOptions.CustomerResourceId` (or per-call override); null, empty, or whitespace values normalize to `"Unknown"` |
| `LocationId` | `ServiceLevelIndicatorOptions.LocationId` |
| `Operation` | Caller-supplied operation name |
| `Outcome` | `Success`, `Failure`, `ClientError`, or `Ignored` |

Additional attributes can be appended via `MeasuredOperation.AddAttribute(...)` or the `attributes` parameter of `Record`/`StartMeasuring`. Custom attributes must not reuse `CustomerResourceId`, `LocationId`, `Operation`, `Outcome`, `activity.status.code`, `http.request.method`, `http.response.status.code`, `sli.request.aborted`, `sli.request.abort_exception`, `sli.latency.threshold_ms`, `sli.latency.result`, or another name already present. Direct `Record(...)` calls emit the required dimensions and supplied custom attributes.

## Optional threshold counter

Configure `options.LatencyThresholds.Add(TimeSpan.FromSeconds(2))` during startup. The initially empty, get-only collection accepts distinct positive whole-millisecond values, validated and snapshotted before instrument creation.

| Element | Contract |
|---|---|
| Instrument / type / unit | `operation.duration.threshold.count` / `Counter<long>` / `{operation}` |
| Value | One increment per operation per threshold across all `Record`, scope, helper, and HTTP paths |
| `sli.latency.threshold_ms` | Integer milliseconds (`long`), e.g. `2000` |
| `sli.latency.result` | `within` when recorded integer milliseconds are `<=` threshold; otherwise `exceeded` |
| Retained tags | Required SLI dimensions plus API version, abort indicator, and custom dimensions when present |
| Omitted tags | `http.request.method`, `http.response.status.code`, `sli.request.abort_exception` |

The duration histogram keeps its original tags and unit. Both instruments use the configured meter; no threshold instrument is created when the collection is empty. Generated threshold-tag names are also rejected on direct `MeasuredOperation.Attributes` mutation.

Thresholds classify recorded duration; they do not enforce timeouts, cancel work, or change an operation's outcome.

Timing is independent of outcome: a fast failure is `within` but not successful-and-fast. Select one threshold and an explicit eligibility policy for each ratio. No eligible traffic is no data; eligible traffic with zero good events is 0. The counter supports configured boundaries, not arbitrary percentiles.

For a fixed meter/resource scope, `N` thresholds and `P` projected base-tag combinations add at most `2 * N * P` series; `P` includes outcome and retained optional/custom dimensions. Backend resource/instance dimensions may multiply this further. On Azure Monitor's delta-export path, sum each interval's `AppMetrics.Sum` increment over the window, not rows, `ItemCount`, latest/max, or cumulative differences. Prometheus cumulative scrapes need `increase`/`rate`.

---

## Types

### ServiceLevelIndicator

**Declaration**

```csharp
public sealed class ServiceLevelIndicator : IDisposable
```

Singleton service that creates and records SLI metrics using an OpenTelemetry `Histogram<long>`. Resolved via DI from `IOptions<ServiceLevelIndicatorOptions>`.

**Disposal and meter ownership.** `ServiceLevelIndicator` owns the `Meter` only when it created it (i.e. when no `Meter` was supplied in `ServiceLevelIndicatorOptions`). On `Dispose()`, an internally-created meter is disposed; a user-supplied meter is never disposed by this class. Because `AddServiceLevelIndicator(...)` registers the type as a singleton, the DI container disposes it at host shutdown — applications do not need to call `Dispose()` manually. `Dispose()` is idempotent.

Configuration is copied at construction, including the threshold list, without modifying caller options. Reusing configuration without a custom meter creates independently owned meters. Later changes to the original options do not update the instance; its options property exposes its effective configuration and actual meter.

**Constants**

| Name | Type | Value | Description |
|---|---|---|---|
| `DefaultMeterName` | `const string` | `"Trellis.SLI"` | Default meter name used when no `Meter` is supplied in options. |
| `UnknownCustomerResourceId` | `const string` | `"Unknown"` | Fallback when the target resource has not been configured or resolved. |

**Properties**

| Name | Type | Description |
|---|---|---|
| `ServiceLevelIndicatorOptions` | `ServiceLevelIndicatorOptions` | Per-instance options copy, including the effective meter. Not the caller's original configuration object. |

**Constructors**

| Signature | Description |
|---|---|
| `public ServiceLevelIndicator(IOptions<ServiceLevelIndicatorOptions> options)` | Validates `LocationId`, `DurationInstrumentName`, and thresholds; snapshots thresholds before creating instruments; uses the supplied meter or creates `"Trellis.SLI"` with the assembly version. Creates the duration histogram, diagnostic counter, and opt-in threshold counter. |
| `public ServiceLevelIndicator(IOptions<ServiceLevelIndicatorOptions> options, ILogger<ServiceLevelIndicator> logger)` | Same initialization with warning logs for scope recording failures. Rejects a null logger. DI selects this overload when logging is registered; the original constructor requires no logging services. |

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public void Record(string operation, long elapsedTime, params KeyValuePair<string, object?>[] attributes)` | `void` | Records a measurement using the configured default `CustomerResourceId` and default outcome `Ignored`. |
| `public void Record(string operation, long elapsedTime, SliOutcome outcome, params KeyValuePair<string, object?>[] attributes)` | `void` | Records a measurement using the configured default `CustomerResourceId` and explicit outcome. |
| `public void Record(string operation, string customerResourceId, long elapsedTime, params KeyValuePair<string, object?>[] attributes)` | `void` | Records a measurement with an explicit `CustomerResourceId` and default outcome `Ignored`. |
| `public void Record(string operation, string customerResourceId, long elapsedTime, SliOutcome outcome, params KeyValuePair<string, object?>[] attributes)` | `void` | Records a measurement with an explicit `CustomerResourceId` and outcome. |
| `public MeasuredOperation StartMeasuring(string operation, params KeyValuePair<string, object?>[] attributes)` | `MeasuredOperation` | Starts a stopwatch-backed measurement; dispose the returned object to record the elapsed time as a metric. |
| `public void Measure(string operation, Action action, params KeyValuePair<string, object?>[] attributes)` | `void` | Measures a synchronous operation and infers `Success`, `Failure`, or `Ignored` for `OperationCanceledException`. |
| `public T Measure<T>(string operation, Func<T> action, params KeyValuePair<string, object?>[] attributes)` | `T` | Measures a synchronous operation and returns its result. |
| `public T Measure<T>(string operation, Func<T> action, Func<T, SliOutcome> classifyOutcome, params KeyValuePair<string, object?>[] attributes)` | `T` | Classifies a successfully returned value and returns that same value. |
| `public Task MeasureAsync(string operation, Func<Task> action, params KeyValuePair<string, object?>[] attributes)` | `Task` | Measures an asynchronous operation and infers outcome before rethrowing exceptions. |
| `public Task<T> MeasureAsync<T>(string operation, Func<Task<T>> action, params KeyValuePair<string, object?>[] attributes)` | `Task<T>` | Measures an asynchronous operation and returns its result. |
| `public Task<T> MeasureAsync<T>(string operation, Func<Task<T>> action, Func<T, SliOutcome> classifyOutcome, params KeyValuePair<string, object?>[] attributes)` | `Task<T>` | Awaits the action, classifies its result synchronously, and returns that same result. |
| `public void Dispose()` | `void` | Disposes the internally-created `Meter` if this instance created it; never disposes a user-supplied meter. Idempotent. Normally invoked by the DI container at host shutdown. |
| `public static string CreateCustomerResourceId(Guid serviceId)` | `string` | Builds a `ServiceTreeId://<guid>` customer resource id. Throws `ArgumentNullException` if `serviceId` is `Guid.Empty`. |
| `public static string CreateLocationId(string cloud, string? region = null, string? zone = null)` | `string` | Builds an `ms-loc://az/<cloud>/<region>/<zone>` location id, omitting empty segments. |

**Result selectors.** `classifyOutcome` runs once after successful action completion, including for null results. Value types and application-defined result types are supported; no Trellis Results package is required. Both delegates are required. Action or selector exceptions propagate and record `Failure`; `OperationCanceledException` records `Ignored`. An undefined selector outcome throws `ArgumentOutOfRangeException` and records `Failure`. A failed action never invokes the selector. Both duration and configured threshold metrics use the selected outcome and supplied attributes. Existing overloads without selectors still infer `Success` on normal completion.

Selector execution is included in the measured duration and threshold comparisons; keep classification cheap and synchronous.

```csharp
var validation = sli.Measure("ValidateOrder", () => ValidateOrder(order),
    result => result.IsValid ? SliOutcome.Success : SliOutcome.ClientError);

var saved = await sli.MeasureAsync("SaveOrder", () => SaveOrderAsync(order, ct),
    result => result.IsSuccess ? SliOutcome.Success : SliOutcome.Failure);
```

The functions and result types in this example are application-defined.

---

### ServiceLevelIndicatorOptions

**Declaration**

```csharp
public class ServiceLevelIndicatorOptions
```

Bound via `IOptions<ServiceLevelIndicatorOptions>`. `LocationId` and `DurationInstrumentName` are required (validated in the `ServiceLevelIndicator` constructor).

**Properties**

| Name | Type | Default | Description |
|---|---|---|---|
| `Meter` | `Meter` | `null` (auto-created) | The meter used to create the duration histogram. Set during startup; read once when the `ServiceLevelIndicator` singleton is constructed. |
| `CustomerResourceId` | `string` | `"Unknown"` | Default `CustomerResourceId` tag value. Can be overridden on each call. When emitted as `Unknown`, the diagnostic counter `sli.diagnostics.unknown_customer_resource_id` is incremented. |
| `LocationId` | `string` | `""` | **Required.** Where the service is running (e.g. `ms-loc://az/public/westus3`). Must not be null, empty, or whitespace. |
| `DurationInstrumentName` | `string` | `"operation.duration"` | **Required.** Histogram instrument name. Must not be null, empty, or whitespace. |
| `LatencyThresholds` | `IList<TimeSpan>` (get-only) | Empty | Startup-only positive, distinct, whole-millisecond thresholds. Snapshotted at construction. Enables the fixed-name threshold counter when nonempty. |
| `ActivityStatusCodeAttributeName` | `string` | `"activity.status.code"` | Obsolete compatibility option. Activity status is no longer emitted as a metric dimension. |
| `AutomaticallyEmitted` | `bool` | `true` | Controls ASP.NET automatic emission; when false, endpoints must explicitly opt in. Exclusion metadata takes precedence in either mode. Manual core measurements are unaffected. |

---

### DI registration

**Declaration**

```csharp
public static IServiceLevelIndicatorBuilder AddServiceLevelIndicator(
    this IServiceCollection services,
    Action<ServiceLevelIndicatorOptions> configureOptions)
```

Registers `ServiceLevelIndicator` as a singleton and configures `ServiceLevelIndicatorOptions`. This lives in the core package, so console apps, workers, and shared libraries do not need to reference the ASP package just to use DI. The returned `IServiceLevelIndicatorBuilder` exposes `Services` and is the chaining point for host packages such as `.AddMvc()`, `.AddHttpMethod()`, and `.AddApiVersion()`.

---

### SliOutcome

```csharp
public enum SliOutcome
{
    Success,
    Failure,
    ClientError,
    Ignored
}
```

Metrics use these exact, case-sensitive names. Default availability includes `Success` and `Failure`; `ClientError` and `Ignored` are excluded. Timing thresholds are independent of outcome. Raw scopes explicitly initialize their outcome to `Ignored`, not the enum's zero value.

---

### MeasuredOperation

**Declaration**

```csharp
public class MeasuredOperation : IDisposable
```

Represents an in-flight measurement. The stopwatch starts in the constructor; disposing records the elapsed milliseconds plus the `Outcome` tag.

**Properties**

| Name | Type | Description |
|---|---|---|
| `Operation` | `string` | Operation name emitted as the `Operation` tag. |
| `CustomerResourceId` | `string` | Customer resource id emitted as the `CustomerResourceId` tag. |
| `Attributes` | `List<KeyValuePair<string, object?>>` | Mutable list of additional OpenTelemetry attributes to emit. |

**Constructors**

| Signature | Description |
|---|---|
| `public MeasuredOperation(ServiceLevelIndicator serviceLevelIndicator, string operation, params KeyValuePair<string, object?>[] attributes)` | Uses the default `CustomerResourceId` from the indicator's options. |
| `public MeasuredOperation(ServiceLevelIndicator serviceLevelIndicator, string operation, string customerResourceId, params KeyValuePair<string, object?>[] attributes)` | Uses an explicit `CustomerResourceId`. |

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public void SetOutcome(SliOutcome outcome)` | `void` | Sets the SLI outcome recorded with the measurement. Default is `Ignored`. Throws immediately for an undefined outcome without changing the previous outcome. |
| `public void SetActivityStatusCode(ActivityStatusCode activityStatusCode)` | `void` | Obsolete compatibility shim that maps `Ok` to `Success`, `Error` to `Failure`, and other values to `Ignored`. |
| `public void AddAttribute(string attribute, object? value)` | `void` | Appends a custom attribute to be emitted with the measurement. Throws if the name collides with a reserved SLI tag. |
| `public void Dispose()` | `void` | Stops the stopwatch and attempts recording once. Terminal even if recording fails; reports recording errors instead of throwing them or retrying. |
| `protected virtual void Dispose(bool disposing)` | `void` | Standard dispose pattern hook. |

**Failure boundary.** Direct `Record(...)` calls still surface invalid arguments and provider errors. `AddAttribute(...)` and `SetOutcome(...)` validate immediately. Legacy direct `Attributes` mutations are validated during recording; invalid measurements are dropped and reported. Scope finalization never replaces an action/selector result or exception with a recording error. Providers can fail after partial emission; no retry or rollback is attempted.

Histogram observations and threshold increments are not atomic: a provider failure after histogram delivery can leave some corresponding threshold counts behind. Reconciliation assumes successful recording.

**Diagnostics.** Scope recording failures emit warning logs through `ILogger<ServiceLevelIndicator>` when available and EventSource provider `Trellis-ServiceLevelIndicators`, event 1 (`MeasurementRecordingFailed`, fields `operation` and `error`). If diagnostic logging throws, event 2 (`DiagnosticLoggingFailed`, fields `operation`, `stage`, `originalError`, `loggingError`) reports the failure without propagating it. These are diagnostic events, not new SLI metric dimensions.

---

### ServiceLevelIndicatorEventSource

```csharp
public sealed class ServiceLevelIndicatorEventSource : EventSource
```

Public diagnostic contract shared by core and ASP.NET Core, without assembly-friend access or lockstep exact-version dependencies. `public static ServiceLevelIndicatorEventSource Log { get; }` exposes its singleton; the provider name is `Trellis-ServiceLevelIndicators`.

| Warning event | ID | Payload fields |
|---|---|---|
| `MeasurementRecordingFailed` | 1 | `operation`, `error` |
| `DiagnosticLoggingFailed` | 2 | `operation`, `stage`, `originalError`, `loggingError` |
| `HttpOutcomeClassificationFailed` | 3 | `operation`, `defaultOutcome`, `error` |
| `EnrichmentFailed` | 4 | `operation`, `enrichmentType`, `error` |

Classifier/enrichment events are emitted independently of `ILogger` filtering or logger failure. Enable an EventSource listener/collector to observe them. A classifier fallback changes outcomes and can change the SLO population; the diagnostic identifies the built-in outcome used instead.

| Public helper | Description |
|---|---|
| `ReportRecordingFailure(string operation, Exception exception)` | Reports event 1. |
| `ReportLoggingFailure(string operation, string stage, Exception? originalException, Exception loggingException)` | Reports event 2; the original exception may be absent. |
| `ReportClassifierFailure(string operation, SliOutcome defaultOutcome, Exception exception)` | Reports event 3; rejects undefined fallback outcomes. |
| `ReportEnrichmentFailure(string operation, string enrichmentType, Exception exception)` | Reports event 4. |

Helpers reject null failure exceptions and guard exception formatting. The corresponding public event methods accept preformatted payload strings. Every exception-text field is truncated to at most 2048 UTF-16 code units, including `...`, without splitting surrogate pairs. Truncation is not redaction: messages/stack traces can contain request data. Apply collection/access/retention controls. `ILogger` still receives the original exception under application logging policy.

---

### IEnrichmentContext

**Declaration**

```csharp
public interface IEnrichmentContext
```

Host-neutral contract implemented by enrichment contexts (e.g. `WebEnrichmentContext` in the ASP package). Lets enrichments add attributes and override the target resource without coupling to a specific host.

**Properties**

| Name | Type | Description |
|---|---|---|
| `Operation` | `string` | The operation name being measured. |

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `void SetCustomerResourceId(string id)` | `void` | Overrides the customer resource identifier for this measurement. |
| `void AddAttribute(string name, object? value)` | `void` | Adds a custom attribute to the measurement. |

---

### IEnrichment&lt;T&gt;

**Declaration**

```csharp
public interface IEnrichment<T>
    where T : IEnrichmentContext
```

Strategy contract for adding attributes to a measurement context. Multiple enrichments are invoked in DI registration order.

**Properties**

| Name | Type | Default | Description |
|---|---|---|---|
| `EnrichOnCancellation` | `bool` (get-only) | `false` | Default interface member. ASP.NET may run opted-in enrichments after request cancellation. Only opt in for bounded, cancellation-safe metadata access without I/O; the canceled token is still passed. Existing implementations retain the default. |

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `ValueTask EnrichAsync(T context, CancellationToken cancellationToken)` | `ValueTask` | Add attributes (or override the customer id) on the context. Exceptions thrown here are caught and logged by the host (in the ASP package). |

---

### ServiceLevelIndicatorMeterProviderBuilderExtensions

**Declaration**

```csharp
public static class ServiceLevelIndicatorMeterProviderBuilderExtensions
```

Helpers for wiring the SLI meter into an OpenTelemetry pipeline.

**Methods**

| Signature | Returns | Description |
|---|---|---|
| `public static MeterProviderBuilder AddServiceLevelIndicatorInstrumentation(this MeterProviderBuilder builder)` | `MeterProviderBuilder` | Adds the default meter `"Trellis.SLI"`. |
| `public static MeterProviderBuilder AddServiceLevelIndicatorInstrumentation(this MeterProviderBuilder builder, string meterName)` | `MeterProviderBuilder` | Adds a specific meter by name. Used when the application configures `ServiceLevelIndicatorOptions.Meter` with a custom meter. |
| `public static MeterProviderBuilder AddServiceLevelIndicatorInstrumentation(this MeterProviderBuilder builder, Meter meter)` | `MeterProviderBuilder` | Convenience overload that reads the meter's name. |

---

## Typical usage

For a hosted OTLP pipeline, install:

```shell
dotnet add package Trellis.ServiceLevelIndicators --prerelease
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

These fragments assume an existing host `builder`. The simulated work uses `Task.Delay`; replace it with the application's work.

```csharp
using OpenTelemetry.Metrics;
using Trellis.ServiceLevelIndicators;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddServiceLevelIndicatorInstrumentation()
        .AddOtlpExporter());

builder.Services.AddServiceLevelIndicator(o =>
{
    o.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    o.CustomerResourceId = "project-blue";
});

// In a worker / job:
Task DoWorkAsync(ServiceLevelIndicator sli, CancellationToken ct) =>
    sli.MeasureAsync("SyncProject", () => Task.Delay(75, ct));
```

`MeasureAsync` records `Success` on completion, `Failure` on exceptions, and `Ignored` for `OperationCanceledException`; exceptions still propagate. Raw scopes cannot detect escaping exceptions and must classify every path explicitly.
