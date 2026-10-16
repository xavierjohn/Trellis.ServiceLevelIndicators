# RFC: Trellis ServiceLevelIndicators metric contract

## Status

Implemented in the pre-1.0 metric contract finalization.

The first-preview additions for TFR-26/27/29 shipped in `10.0.0-preview.37` and completed consumer acceptance. TFR-28 result selectors and TFR-30 endpoint exclusion are implemented in the source tree but are not part of preview.37.

This is a design record, not the getting-started guide. The metric and instrumentation-failure contracts below are implemented; the Results adapter, analyzers, testing package, and bucket helper remain future work. Use the [live demo](../../README.md#try-the-live-demo) and [integration reference](../usage-reference.md) for current setup and runnable examples.

## Context

`Trellis.ServiceLevelIndicators` is part of the Trellis AI-first .NET framework. Its role is to provide structural observability guardrails for generated and hand-written services by emitting service-level latency metrics with stable, meaningful dimensions.

The library is currently in the .NET 10 preview/alpha phase. The changes in this RFC finalize the intended pre-1.0 metric contract; no migration guide is required for previous alpha behavior.

## Standards and platform alignment

The design aligns with:

| Source | Usage |
|---|---|
| Google SRE | SLI/SLO semantics, valid requests, excluded traffic, error budgets |
| OpenTelemetry | .NET metric emission, histograms, meter registration, optional HTTP/API dimensions |
| OpenSLO | Documentation examples for portable SLO definitions; no runtime support initially |
| Microsoft Azure Monitor / Application Insights | KQL examples and Azure operational guidance |
| Trellis backend contract | Required dimensions and SLI-focused naming |

## Metric

The primary metric remains:

| Metric | Type | Unit | Required dimensions |
|---|---|---|---|
| `operation.duration` | Histogram | `ms` | `CustomerResourceId`, `LocationId`, `Operation`, `Outcome` |

`operation.count` is not added initially. Success-rate and availability queries should use the histogram count produced by `operation.duration`. A separate count metric may be reconsidered only if backend constraints prove histogram count is insufficient.

`operation.duration` is a custom Trellis SLI metric, not an OpenTelemetry semantic-convention metric. The unit is intentionally milliseconds (`ms`), even though OpenTelemetry HTTP duration semantic conventions use seconds.

### Optional threshold counter

`ServiceLevelIndicatorOptions.LatencyThresholds` enables `operation.duration.threshold.count` (`Counter<long>`, unit `{operation}`), with no thresholds enabled by default. Positive, distinct, whole-millisecond values are validated and snapshotted before instrument creation.

Each operation increments once per threshold, tagged with integer `sli.latency.threshold_ms` and `sli.latency.result = within | exceeded`; equality with the recorded millisecond duration is within. All recording paths share the central emission logic. Timing is independent of outcome.

Threshold configuration classifies recorded durations only; it does not impose a deadline, cancel work, or change HTTP status/outcome.

The counter retains required dimensions, API version, the abort indicator, and custom dimensions when present. It omits `http.response.status.code`, `http.request.method`, and `sli.request.abort_exception`. It does not add threshold tags to the duration histogram.

For a fixed meter/resource scope, `N` thresholds and `P` projected base-tag combinations add at most `2 * N * P` series. Backend resource/instance dimensions may multiply this further. Per-operation threshold overrides remain deferred.

For Azure Monitor delta exports, sum counter `AppMetrics.Sum` interval increments over the window, not rows, `ItemCount`, latest/max, or cumulative differences. Prometheus cumulative scrapes need `increase`/`rate`. A configured threshold ratio cannot reconstruct arbitrary percentiles.

## Required dimensions

These dimensions are mandatory and emitted exactly:

- `CustomerResourceId`
- `LocationId`
- `Operation`
- `Outcome`

Dimension sources:

| Dimension | Source / fallback |
|---|---|
| `CustomerResourceId` | Explicit/default resource identifier; fallback `Unknown` |
| `LocationId` | Required startup configuration; fail fast when missing or empty |
| `Operation` | Caller-supplied operation; ASP.NET uses explicit override, route template, then `<METHOD> <unrouted>` |
| `Outcome` | Explicit outcome, helper inference, middleware inference, or default `Ignored` |

Manual/background operations throw `ArgumentException` when `Operation` is null, empty, or whitespace. ASP.NET unrouted fallback uses uppercase HTTP method formatting, for example `GET <unrouted>`.

`LocationId` is startup-only. It cannot be overridden per operation or request.

Optional HTTP/API dimensions use OpenTelemetry-style names, including:

- `http.request.method`
- `http.response.status.code`
- `http.api.version`

The required PascalCase dimensions intentionally deviate from OpenTelemetry attribute naming conventions because they are part of the Trellis backend contract.

## Outcome model

C# code should expose:

```csharp
public enum SliOutcome
{
    Success,
    Failure,
    ClientError,
    Ignored
}
```

Metrics emit the dimension:

```text
Outcome = "Success" | "Failure" | "ClientError" | "Ignored"
```

Emit outcome values through explicit string mapping. Do not rely on enum `ToString()` for the wire value.
Wire values are case-sensitive.

SLO semantics:

| Outcome | Meaning | SLO usage |
|---|---|---|
| `Success` | Operation completed successfully | Numerator and denominator |
| `Failure` | Service failed to satisfy the operation | Counted as a valid event; not counted as good |
| `ClientError` | Client/request/input problem, reported separately | Excluded from default success-rate denominator |
| `Ignored` | Not part of SLI measurement | Excluded |

Default success-rate formula:

```text
success_rate = count(Outcome == "Success") / (count(Outcome == "Success") + count(Outcome == "Failure"))
```

`ClientError` and `Ignored` are excluded from the default denominator. Services may report `ClientError` separately or opt into policy-specific SLO formulas.

Outcome precedence:

1. Explicit user-set outcome.
2. Helper inference.
3. Middleware inference.
4. Default `Ignored`.

Observed exceptions follow the helper and HTTP rules below. Raw `StartMeasuring` scopes cannot detect escaping exceptions.

## ASP.NET classification defaults

| HTTP status/result | Outcome |
|---|---|
| 2xx | `Success` |
| 3xx | `Success` |
| 400, 401, 403, 404, 409, 412, 422 | `ClientError` |
| 429 | `Failure` by default; configurable |
| 5xx | `Failure` |
| Observed request abort, regardless of escaping exception shape | `Ignored` |
| Escaping exception without a request abort | `Failure` |

Application/business cancellations remain configurable.

429 reduces success rate by default because throttling is treated as a service capacity/backpressure signal. Services that treat 429 as client-driven may classify it as `ClientError`.

3xx responses are `Success` by default because redirects and cache-validation responses can be successful service behavior. Redirect loops should be monitored through status-code queries or classifier overrides.

HTTP precedence is observed abort, then escaping exception, then normal-response classification. For an escaping exception without an abort, preserve started responses and existing status >= 500; otherwise set status 500 before headers start. Aborts never manufacture 499 or an abort-related 500.

On an abort, emit `sli.request.aborted = true` and omit unsent `http.response.status.code`; preserve the actual status after headers start. Omit the abort indicator for non-aborted requests. If an exception escapes during the abort, add duration-only `sli.request.abort_exception = cancellation` for top-level `OperationCanceledException` (including derived types), otherwise `other`. Omit this diagnostic without an abort or escaping exception. It describes shape, not cause; do not walk exception trees to infer fault.

Abort-first availability may hide slow dependency outages when the caller gives up before the server fails. Pair availability with a successful-and-fast latency objective including slow aborts. The exception-shape diagnostic supports investigation, not reliable standalone outage detection.

ASP.NET emits these optional dimensions by default:

- `http.request.method`
- `http.response.status.code`

API version remains opt-in through `.AddApiVersion()` and emits:

- `http.api.version`

## Manual measurement

Manual/background operations default to `Outcome = Ignored` unless explicitly set or inferred by helper APIs.

Core APIs should include outcome-oriented methods such as:

```csharp
measuredOperation.SetOutcome(SliOutcome.Success);
```

Core `Measure(...)` and `MeasureAsync(...)` helpers without a result selector infer:

- `Success` on normal completion.
- `Failure` on exception.
- `Ignored` on `OperationCanceledException`.

Generic sync and `Task<T>` overloads also accept `Func<T, SliOutcome> classifyOutcome`. The selector runs once after successful completion, accepts the original result including null, and returns a defined outcome. The original result is returned unchanged. Both metrics use the selected outcome; action exceptions bypass the selector. Action/selector exceptions propagate with `Failure`, while `OperationCanceledException` records `Ignored`. Undefined selector outcomes are rejected before assignment and record `Failure`. Existing overloads keep their previous inference.

Core helpers are not ASP.NET-specific and do not detect client disconnects. ASP.NET middleware maps `HttpContext.RequestAborted` / request-aborted cancellation to `Ignored`; application/business cancellations remain configurable.

Automatic Trellis `Result<T>` mapping belongs in a future optional `Trellis.ServiceLevelIndicators.Results` adapter, not in the core package. Generic core result selectors remain type-agnostic.

## Activity correlation

`activity.status.code` is removed from default metric dimensions.

The library should continue updating `Activity.Current` status for trace correlation. Activity status is not the source of SLI outcome.

Activity status mapping:

| Outcome | Activity status |
|---|---|
| `Success` | `Ok` |
| `Failure` | `Error` |
| `ClientError` | `Unset` |
| `Ignored` | `Unset` |

## Unknown customer resource

When no customer resource is known, emit:

```text
CustomerResourceId = "Unknown"
```

Diagnostics:

- Log a one-time warning per operation/location when `CustomerResourceId = Unknown`.
- Increment `sli.diagnostics.unknown_customer_resource_id` every time `CustomerResourceId = Unknown`.
- Do not throw, drop, cap, or replace required dimensions by default.

`Unknown` is deliberate and distinct from `ActivityStatusCode.Unset`.

The one-time warning scope is per process lifetime. The implementation should bound the warning cache to avoid unbounded memory growth.

Diagnostic counter contract:

| Instrument | Type | Dimensions |
|---|---|---|
| `sli.diagnostics.unknown_customer_resource_id` | `Counter<long>` | `Operation`, `LocationId` |

## Meter

The default meter name remains:

```text
Trellis.SLI
```

Custom meter registration remains supported. All SLI metrics and diagnostic counters should use the configured meter consistently.

## Implementation design

### Core recording

`ServiceLevelIndicator` creates the `operation.duration` histogram, `sli.diagnostics.unknown_customer_resource_id` counter, and optional threshold counter. All use the default `Trellis.SLI` meter or the configured custom meter.

Every duration recording emits:

- `CustomerResourceId`
- `LocationId`
- `Operation`
- `Outcome`
- any custom dimensions

Custom dimensions must not reuse required names, reserved names, or optional dimensions already present on the measurement. Supported attribute APIs reject schema collisions immediately. Legacy raw-list mutations are validated at recording time and malformed scope measurements are dropped with diagnostics, without throwing from disposal. The diagnostics-only suspicious-value rule does not permit schema collisions.

### MeasuredOperation

`MeasuredOperation` stores `SliOutcome`, defaulting to `Ignored`.

Public API:

```csharp
measuredOperation.SetOutcome(SliOutcome.Success);
```

Internal behavior:

- Disposing attempts to record elapsed milliseconds once.
- Disposing records the explicit or inferred `Outcome`.
- Raw `StartMeasuring(...)`/`Dispose()` cannot detect escaping exceptions and records the explicit/default outcome.
- Core helpers infer failure for non-cancellation exceptions; HTTP middleware infers failure for escaping exceptions only without an observed request abort.
- `Dispose()` becomes terminal before recording and remains idempotent even on failure or reentrant disposal.
- Recording failures are warning-logged and reported through EventSource, never retried or thrown over an application exception. Provider failures may leave partial emission.
- Direct `Record(...)`, `AddAttribute(...)`, and `SetOutcome(...)` validation remains strict; undefined outcomes are rejected before recording side effects.
- `SetOutcome(...)` after disposal has no effect.

### Helper APIs

Core helper APIs infer outcomes:

| Result | Outcome |
|---|---|
| Normal completion without a result selector | `Success` |
| Normal completion with a result selector | Selected, defined `SliOutcome` |
| Action or selector exception | `Failure` |
| `OperationCanceledException` | `Ignored` |

Helpers rethrow exceptions and cancellations after setting the inferred outcome.
Rethrows should use `throw;` so exception stack traces are preserved.

If user code explicitly sets `Failure` inside a helper and completes normally, explicit `Failure` remains. Helper success inference does not promote it to `Success`. Unhandled exceptions observed by helpers still force `Failure`.

### ASP.NET middleware

Middleware owns ASP.NET request measurement. Core helpers and ASP.NET middleware normally do not co-apply to the same `MeasuredOperation`; precedence exists for explicit outcomes, inferred outcomes, and future extension points.

Middleware should:

- skip automatic measurement for `[ExcludeFromServiceLevelIndicator]` or `.ExcludeFromServiceLevelIndicator()` metadata before resolving operation/attributes, starting a scope, attaching a feature, or running enrichments,
- resolve `Operation` from explicit metadata, route template, then uppercase `<METHOD> <unrouted>`,
- set `CustomerResourceId` from endpoint metadata when present,
- default missing customer resource to `Unknown`,
- classify abort first, escaping exception next, then configured/default normal-response mapping,
- emit HTTP and abort dimensions with the status-presence rules above,
- run enrichments before recording, skipping canceled work unless it explicitly opts into bounded metadata access,
- rethrow escaping exceptions, preserving their stack traces and abort-first outcome.

Exclusion applies to MVC controllers/actions, Minimal API endpoints, route groups, and health-check mappings. Controller/action attributes and group metadata are inherited; exclusion wins over automatic and explicit inclusion. Excluded endpoints produce no automatic duration, threshold, or measurement-related diagnostic counts on any completion path. Response behavior, platform telemetry, and explicit core measurements are unchanged.

### Classifier extensibility

Global normal-response classifier APIs:

```csharp
.ClassifyHttpOutcome(Func<HttpContext, SliOutcome> classifier)
.ClassifyHttpOutcome(Func<HttpContext, SliOutcome, SliOutcome> classifier)
```

The first overload retains replacement semantics; the second receives the built-in outcome so callers can preserve defaults. `HttpOutcomeClassifier.ClassifyStatusCode(int)` exposes the default mapper. Last fluent registration wins. With direct options configuration, `ClassifyOutcomeWithDefault` takes precedence over `ClassifyOutcome`. Neither callback runs on an observed abort or escaping exception. Per-route APIs remain deferred.

HTTP classifier exceptions and undefined outcomes are logged and use the already-computed built-in status outcome; enrichment continues. Do not try a lower-priority classifier. This ambient HTTP failure boundary does not change explicit core action/result-selector propagation.

Fallback can change the SLO population, not merely the diagnostic stream: custom-ignored `/health` may become `Success`, and a custom-client-error 429 may become `Failure`. Always emit classifier-failure event 3 with the fallback outcome, independently of warning-log filtering.

### Configuration and diagnostic failure boundaries

Each indicator copies its supplied options and threshold collection. Effective options retain access to the instance's actual meter, while caller configuration remains unchanged. Reusing options without a supplied meter creates independently owned meters; supplied meters remain caller-owned. Later mutations of the original options do not update an existing indicator.

Scope recording failures use `ILogger<ServiceLevelIndicator>` when available and EventSource provider `Trellis-ServiceLevelIndicators`, event 1 (`MeasurementRecordingFailed`). The original constructor remains supported without a logging dependency in application setup; DI can supply the logger through the additional constructor. All diagnostic logging is guarded. A broken logger emits event 2 (`DiagnosticLoggingFailed`) rather than changing HTTP behavior or masking a primary exception. No metric names, units, or dimensions are added by this policy.

The provider is the public `ServiceLevelIndicatorEventSource.Log` contract. ASP uses this and public `MeasuredOperation.SetOutcome` validation, not core internals; compatible core upgrades remain possible without exact-version pins. Diagnostic helpers take arguments directly rather than captured delegates, so successful requests do not allocate error-reporting closures.

Event 3 (`HttpOutcomeClassificationFailed`) includes `operation`, `defaultOutcome`, and `error`. Event 4 (`EnrichmentFailed`) includes `operation`, `enrichmentType`, and `error`. Both emit independently of `ILogger`; an enabled EventSource collector/listener is required to observe them.

Every exception-text field is bounded to 2048 UTF-16 code units, including the truncation marker, without splitting surrogate pairs. This is not redaction: exception messages and stack traces may contain request data. Apply collection/access/retention controls. `ILogger` still receives the original exception under application policy.

Provider failures can leave histogram observations without some corresponding threshold increments. Cross-instrument emission is not atomic, so histogram/counter reconciliation assumes recording succeeded; no retry or rollback hides a failed partial emission.

### API versioning

The API versioning package remains an enrichment package. It continues to emit `http.api.version` only when `.AddApiVersion()` is called and does not participate in outcome classification.

`ApiVersionEnrichment.EnrichOnCancellation` is true, retaining already-resolved versions on aborted requests. The default interface value on `IEnrichment<T>` is false, preserving existing user-enrichment cancellation behavior. Only bounded, cancellation-safe metadata access without I/O should opt in; the canceled token is still passed. Skip individual unsafe enrichments rather than terminating the whole sequence.

## Dimension stability guardrails

The rule is stable and meaningful dimensions, not low cardinality.

`CustomerResourceId` may legitimately have very high cardinality at Microsoft/Azure scale, such as user object IDs in login services. Required dimensions must pass through exactly by default.

Default behavior:

- Required dimensions: diagnostics only; never mutate by default.
- Optional/custom dimensions: diagnostics only by default.
- Strict blocking or replacement: opt-in only.
- Runtime suspicious-value heuristics are off by default to avoid hot-path overhead at high scale; analyzers and documentation are the default guardrails.

Diagnostics should look for unstable values such as:

- request IDs,
- timestamps,
- raw paths,
- arbitrary text,
- generated-per-request GUIDs,
- emails when a stable object ID is available.

Analyzer support is a later milestone.

## Package architecture

Core SLI package remains independent from `Trellis.Core`.

Optional Trellis integration package:

```text
Trellis.ServiceLevelIndicators.Results
```

Dependency direction:

```text
Trellis.ServiceLevelIndicators.Results
    -> Trellis.ServiceLevelIndicators
    -> Trellis.Core
```

Neither core package depends on the Results integration package.

## Documentation requirements

Docs should include:

- Google SRE-aligned SLO semantics.
- OpenTelemetry metric setup.
- OpenSLO examples, documentation only.
- Azure Monitor / Application Insights KQL examples.
- Success-rate queries using `operation.duration` histogram count.
- Latency percentile queries.
- Client-error rate queries.
- Unknown `CustomerResourceId` detection.
- OpenTelemetry View/cardinality-limit guidance for services that intentionally use high-cardinality `CustomerResourceId` values.

## Testing support

Use TDD by phase: write or adjust failing tests first, implement the smallest passing change, then refactor.

Core and ASP.NET tests should cover outcome values, diagnostics, exception/cancellation paths, Activity status mapping, required dimensions, and custom meter behavior. Custom meter tests must verify both `operation.duration` and `sli.diagnostics.unknown_customer_resource_id` use the configured meter.

Eventually add:

```text
Trellis.ServiceLevelIndicators.Testing
```

This package should provide metric assertion helpers for AI-generated and hand-written tests.

## Histogram views/buckets

Provide optional helper APIs and documentation for recommended API latency histogram views/buckets. Do not force bucket configuration by default.

## Alternatives considered

### Keep `activity.status.code` as the primary outcome dimension

Rejected. `activity.status.code` is trace-oriented and does not model SLI-specific states such as `ClientError` and `Ignored` clearly enough for SLO math. The library should still update `Activity.Current` for trace correlation.

### Add `operation.count` immediately

Rejected for the first implementation. Histograms already produce a count series, and a separate counter risks drift unless every recording path updates both instruments consistently.

### Use seconds instead of milliseconds

Rejected. OpenTelemetry HTTP semantic-convention duration metrics use seconds, but Trellis SLI metrics intentionally emit latency in milliseconds to match the backend contract and existing library positioning.

## Open questions

- Exact recommended histogram bucket boundaries for the optional helper.
- Exact per-route classifier API shape.
- Exact analyzer packaging and suppression model for the later analyzer milestone.
