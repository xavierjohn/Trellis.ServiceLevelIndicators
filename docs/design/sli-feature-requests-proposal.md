# Proposal: SLI feature requests TFR-26 through TFR-30

**Status:** TFR-26/27/29 implemented locally following two consumer reviews; preview publication and consumer validation pending. TFR-28/30 remain proposed.
**Date:** 2026-10-15.
**Repository:** `xavierjohn/Trellis.ServiceLevelIndicators`.
**Input:** Saborly's feature requests while using `10.0.0-preview.32` and its two proposal reviews of 2026-10-15.

## Summary

We propose addressing all five underlying needs, while adjusting some of the suggested API shapes.

The central principle is to **record what happened and make explicit which events an SLO counts**. An HTTP status, an operation outcome, a request abort, and a latency threshold answer different questions. Collapsing them into one classification can hide service failures or make unrelated SLOs share the wrong denominator.

| Request | Proposed response |
| --- | --- |
| TFR-26: Change one HTTP classification | Add a default-aware classifier overload and publish the default status mapper. Retain the existing classifier API. |
| TFR-27: Aborted requests appear as 200 | Record an explicit abort indicator. Omit the status when no response started; preserve the actual status after headers started. Keep default availability treatment unchanged. |
| TFR-28: Failures returned as values | Add generic outcome-selector overloads to the core measurement helpers. Keep Trellis-specific result mapping in an optional adapter. |
| TFR-29: Histogram buckets lost on export | Add opt-in threshold counting alongside the existing histogram, recording both within-threshold and over-threshold events. |
| TFR-30: Exclude individual endpoints | Add exclusion metadata for MVC, Minimal APIs, route groups, and health-check mappings. |

The preferred telemetry choices are a **companion threshold counter**, rather than another dimension on the existing histogram, and **omitting an unsent status**, rather than recording synthetic 499.

Saborly accepted this direction, including the default-aware classifier, omitted unsent status, and self-contained threshold ratio. This revision incorporates its concerns about wrapped cancellations, counter cardinality, future per-operation thresholds, and instrument naming. Saborly's second review of the revision accepted the abort-first resolution and raised two further points, both incorporated here: abort-first classification can hide a slow-failure outage, so aborted measurements gain a non-causal exception diagnostic, and the wording about counter `Sum` values is corrected to say each exported row holds that interval's increment.

The first-preview API and wire contracts for TFR-26/27/29 are now implemented. TFR-28/30 APIs below describe future work and are not yet supported.

## Compatibility and scope

The proposal preserves:

- The `operation.duration` histogram and its millisecond unit.
- The required `CustomerResourceId`, `LocationId`, `Operation`, and `Outcome` dimensions.
- The existing `Success`, `Failure`, `ClientError`, and `Ignored` outcome wire values.
- Default availability arithmetic: `Success / (Success + Failure)`.
- Existing HTTP classification defaults, including 429 as `Failure`.
- Abort-first HTTP classification: an observed request abort remains `Ignored`, including when the escaping exception is wrapped or provider-specific.
- Existing Activity status mapping, without reintroducing `activity.status.code` as a metric dimension.
- Custom meter registration and ownership.
- A core package usable without ASP.NET Core, Trellis result types, or any particular exporter.

The abort-status correction intentionally changes the emitted status dimension on aborted requests whose responses have not started. It must be documented as an observable behavior change. It does not change the response sent by the application or the default availability denominator.

This work does not add an SLO definition language, error-budget engine, alerting system, or arbitrary percentile reconstruction.

## TFR-26: Make HTTP classification composable

### Proposed design

Add an overload that supplies the built-in outcome to the application's classifier:

```csharp
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = locationId;
})
.ClassifyHttpOutcome((context, defaultOutcome) =>
    context.Response.StatusCode == StatusCodes.Status429TooManyRequests && IsCallerQuotaRejection(context)
        ? SliOutcome.ClientError
        : defaultOutcome);
```

Keep the existing one-argument overload and its replacement semantics for compatibility. Publish the built-in status mapping through a public helper:

```csharp
SliOutcome defaultOutcome = HttpOutcomeClassifier.ClassifyStatusCode(statusCode);
```

The new callback receives the built-in status classification for a completed request. Exceptional completion and request aborts are handled separately. An escaping exception without a request abort remains `Failure`; the classifier must not convert it into success.

`IsCallerQuotaRejection` represents application-specific limiter metadata, not another library API.

Last fluent classifier registration wins across both overloads. When configuring both options properties directly, `ClassifyOutcomeWithDefault` takes precedence over the existing `ClassifyOutcome`.

### Why this shape

A nullable callback and a per-status override dictionary are both viable. A default-aware callback provides fallback directly, supports contextual decisions, and avoids requiring either a null sentinel or a second configuration mechanism.

The existing metric-contract RFC already calls for classification fallback. This proposal closes that extensibility gap while retaining the existing delegate signature.

### Policy guidance for 429

Keep 429 as `Failure` by default:

- Exceeding a contractual caller quota can reasonably be `ClientError`.
- Rejecting otherwise valid traffic because the service lacks capacity is an availability failure.

An intentional rejection is not, by itself, evidence that the caller is responsible. Applications using both kinds of rate limiting should classify them using known limiter or application context, rather than globally excluding all 429 responses.

## TFR-27: Describe aborts without inventing a successful response

### Proposed telemetry

For a request observed as aborted, add the bounded indicator `sli.request.aborted = true`. Omit it for non-aborted requests; queries treat absence as false.

| Completion | `Outcome` | `http.response.status.code` |
| --- | --- | --- |
| Normal completion | Existing classification | Existing response status |
| Request aborted before response headers started | `Ignored` | Omitted |
| Request aborted after response headers started | `Ignored` | Preserve the status associated with the started response |
| Unhandled exception without a request abort | `Failure` | Preserve existing exception-status handling |

For an aborted request through which an exception escaped, also record the bounded diagnostic `sli.request.abort_exception`, with one of two values:

| Value | Meaning |
| --- | --- |
| `cancellation` | The escaping exception is an `OperationCanceledException` (including `TaskCanceledException`). |
| `other` | Any other exception type, including a provider cancellation wrapped in `SqlException`, `HttpRequestException`, or `AggregateException`. |

Omit it when the request was aborted but no exception escaped, and for requests that were not aborted. It never changes `Outcome`, and it is not evidence of cause: `other` includes wrapped cancellations, so it marks aborts worth inspecting rather than proving an independent fault. See "Outage correlation" below.

### Abort-first exception policy

Retain the current classification precedence:

1. If `RequestAborted` is canceled when HTTP outcome classification runs, use `Ignored`, regardless of the outer exception type.
2. Otherwise, an escaping exception is `Failure`.
3. Otherwise, classify the completed response using the configured callback or built-in mapping.

This revises the earlier proposal's unconditional statement that an independently failing exception must win over a canceled abort token. The middleware cannot reliably establish that causal relationship from an exception type or exception tree alone.

Provider cancellations can surface as `SqlException`, `HttpRequestException`, wrappers around `OperationCanceledException`, or `AggregateException`. Do not infer an independent service failure merely because the outer exception is not `OperationCanceledException`, or because no inner cancellation exception is present.

Conversely, an inner cancellation exception is not sufficient to ignore a request whose abort token was not canceled. One canceled branch of an `AggregateException` also does not establish that every branch is cancellation-related. An exception-tree walk can help diagnosis, but is not a prerequisite for this token-based default.

Use the same abort decision consistently for abort-related status handling; an outer wrapper alone must not turn an observed abort into a synthetic 500. Exceptions still propagate to the caller and normal exception telemetry.

The tradeoff is explicit: abort-first classification can exclude an independent failure that coincides with an abort. Preserve compatibility rather than claim the library can distinguish those cases automatically. Defer an application-specific independent-fault override API until a concrete requirement justifies it.

#### Outage correlation

The coincidence is not independent of outages. When a dependency such as a database fails slowly, with long connect timeouts and retries, callers or an intermediary often give up before the server's exception arrives. Every such request is then `Ignored`, and neither availability nor a failures alert sees the outage. This is today's behavior, not something the revision introduces, but the documentation should not describe it as a rare overlap.

The mitigations are deliberately outside the outcome:

- `sli.request.abort_exception` lets a query surface aborts that ended in something other than a plain cancellation, without the library asserting that they were faults.
- A latency objective that counts aborts which already exceeded the threshold (see the pilot policy below) catches the same outage from the timing side.
- Document both, and pair availability with the latency objective; the exception-shape diagnostic supports investigation, not reliable standalone outage detection. Keep the diagnostic on the duration measurement and omit it from the threshold counter (see the projection below), because an eligibility policy does not need it.

This policy is specific to HTTP instrumentation. Core measurement helpers retain their existing exception and cancellation behavior.

The abort indicator describes an observed condition, not a proven cause. `RequestAborted` does not establish whether the caller navigated away, a proxy timed out, or the application aborted the connection.

Do not modify `HttpContext.Response.StatusCode` merely to make telemetry say 499. Synthetic 499 is a useful convention in some systems, but it is not necessarily a status the application transmitted. Omitting an unsent status is the preferred representation.

### Preserve useful grouping

Aborted requests currently skip post-request enrichments. Known operation, resource, and API-version metadata needed for SLO grouping should remain available on aborted measurements. Otherwise, fixing the abort flag alone still leaves cancellations missing from customer- or version-specific queries.

Saborly's review reports missing API versions on 13 of 14 aborted metric rows, compared with 1 of 1,125 non-aborted rows. These are aggregated metric rows, not individual-request counts, but the contrast reinforces the need to preserve grouping metadata.

This must not require running arbitrary user-supplied asynchronous enrichment after cancellation or fabricating dimensions that were never resolved.

The implementation uses `IEnrichment<T>.EnrichOnCancellation`, a default interface property returning false. `ApiVersionEnrichment` opts in because it only accesses bounded metadata without I/O. The middleware skips unsafe enrichments individually, allowing later safe metadata enrichments to run with the canceled token. Existing `Enrich`/`EnrichAsync` delegates retain their skip-on-cancellation behavior.

Coverage must include wrapped and aggregate exceptions, plus real mid-flight SqlClient and HttpClient cancellation scenarios, not only a manually thrown `OperationCanceledException`. Provider-specific dependencies belong in integration fixtures, not in the core library or ASP.NET runtime package.

### Measurement boundary

The duration remains application-processing duration measured by the middleware. It does not include all waiting before the application pipeline, and a proxy timeout does not necessarily propagate as `RequestAborted`.

This change improves the truthfulness of service telemetry. It does not make server-side timing equivalent to an end-to-end customer measurement; synthetic or client-side telemetry remains useful for that purpose.

## TFR-28: Classify returned values without coupling the core to Trellis

Add generic overloads alongside the existing helpers:

```csharp
public T Measure<T>(
    string operation,
    Func<T> action,
    Func<T, SliOutcome> classifyOutcome,
    params KeyValuePair<string, object?>[] attributes);

public Task<T> MeasureAsync<T>(
    string operation,
    Func<Task<T>> action,
    Func<T, SliOutcome> classifyOutcome,
    params KeyValuePair<string, object?>[] attributes);
```

On normal completion, classify the returned value and return that same value to the caller. Preserve the existing handling of exceptions and cancellations from the measured action, including propagation to the caller. Selector failures must surface, not silently produce a success measurement.

The initial addition should match the existing synchronous and `Task`-based surfaces. Additional async shapes need not be introduced without a demonstrated use case.

Trellis `Result` and `Error` presets belong in the optional `Trellis.ServiceLevelIndicators.Results` integration already envisioned by the metric-contract RFC. The core package must not depend on `Trellis.Core`.

Result mapping is still policy: an unavailable provider generally indicates a service failure, while validation failures generally indicate caller input. Business-specific cases such as not-found require care rather than a universal assumption.

The generic selector should ship independently of that adapter so applications can remove their measurement boilerplate immediately.

## TFR-29: Support threshold SLIs when the exporter loses buckets

### Confirmed constraint

The Azure Monitor exporter converts histogram observations into sum, count, and optional min/max values. It does not retain the histogram bucket distribution in Application Insights metrics.

Those aggregates support counts and averages, but cannot reconstruct an exact fraction below an arbitrary threshold or a latency percentile. Changing OpenTelemetry histogram boundaries alone does not solve a downstream exporter dropping the buckets.

### Alternatives considered

| Approach | Benefit | Limitation |
| --- | --- | --- |
| Use a bucket-preserving backend | Retains distributions and supports percentile estimates | Does not solve the team's existing Application Insights path |
| Add an opt-in latency-band dimension to `operation.duration` | Preserves threshold membership without a second instrument | Splits histogram series and changes their grouping and aggregation cost |
| Add a companion threshold counter | Preserves the existing histogram and exports portable event counts | Adds bounded metric series and requires explicit threshold configuration |
| Add SLI outcome to request activities | Helps request-level diagnostics | Sampled request telemetry is not a dependable SLO denominator |

Prefer the companion counter. Activity enrichment can be considered separately, but is not a substitute for metric-based threshold counts.

### Proposed configuration and instrument

```csharp
options.LatencyThresholds.Add(TimeSpan.FromSeconds(2));
options.LatencyThresholds.Add(TimeSpan.FromSeconds(10));
```

These are Saborly's initial global thresholds, not library defaults.

| Element | First-preview contract |
| --- | --- |
| Instrument | `operation.duration.threshold.count` |
| Type | `Counter<long>` |
| Unit | `{operation}` |
| Enablement | Opt-in; no thresholds configured by default |
| Base dimensions | Required SLI dimensions plus retained optional dimensions, as specified below |
| Threshold dimension | `sli.latency.threshold_ms`, for example `2000` |
| Timing dimension | `sli.latency.result`, either `within` or `exceeded` |
| Boundary | `within` means recorded elapsed milliseconds are less than or equal to the threshold |

Use positive, whole-millisecond thresholds configured at startup. Reject invalid or duplicate thresholds rather than silently rounding them. Snapshot the configuration for the lifetime of the instrument.

For each measured operation and each configured threshold, record exactly one increment: either `within` or `exceeded`. This timing classification is independent of `Outcome`; a failed operation can finish quickly.

The revised name makes clear that the instrument counts operation-duration observations relative to a threshold, rather than measuring the threshold itself. It also groups alongside `operation.duration`. The name, unit, tag names, and boundary above are the implemented first-preview contract.

Generate the duration and threshold measurements from the same central recording path after final dimensions and outcome are available. This must cover direct `Record`, measurement scopes, helper APIs, and HTTP middleware, and must use the configured meter consistently.

### Counter dimensions and cardinality

Use a smaller fixed projection for the counter:

- Always retain `CustomerResourceId`, `LocationId`, `Operation`, and `Outcome`.
- Retain the abort indicator, API version, and other custom dimensions when present, preserving their values.
- Omit `http.response.status.code` and `http.request.method` from the counter by default.
- Omit `sli.request.abort_exception` from the counter. It is a diagnostic on the duration measurement, not an input to an eligibility policy, and retaining it would multiply series without changing any ratio.
- Leave the duration histogram and its HTTP dimensions unchanged.

Status code is not needed for the selected good/eligible-event policy, and normal HTTP operation names already contain the method. If an application deliberately gives multiple methods the same operation name, its threshold counts aggregate under that operation. API version remains available for version-specific SLOs.

The initial design uses this fixed projection rather than adding a general tag-selection API. It does not drop or rewrite required dimensions to control cardinality.

For a fixed meter/resource scope, with `N` thresholds and `P` distinct projected base-dimension combinations, the counter adds at most:

```text
2 * N * P series
```

`P` includes `Outcome`, the abort indicator when present, API version, customer, location, operation, and retained custom dimensions; it excludes the threshold and timing-result dimensions. With Saborly's two thresholds, the bound is `4 * P`. Backend resource or instance dimensions can further multiply deployment-wide series. This is a bound on additional counter series within the stated scope, not a bound on customer or custom-dimension cardinality.

### Global thresholds and future operation-specific configuration

Initially, the configured thresholds apply to all measured operations; queries select the relevant `Operation` and threshold. Avoid runtime-generated thresholds, per-request objective names, or a general policy engine.

An optional per-operation override can follow later without changing the instrument or tag contract. The wire format and queries must not assume that every operation will always emit every globally configured threshold. The override API and replacement/merge semantics need a separate design; they are not a blocker for the initial preview.

### Denominator and query requirements

Improve on a good-events-only counter by recording both sides:

```text
timing compliance = within / (within + exceeded)
```

Both sides must select the same threshold, eligible event population, dimensions, and time window. This avoids coupling a new counter numerator to a differently filtered histogram denominator.

For a successful-and-fast objective, the numerator must additionally require `Outcome = Success`. A fast 500 must not count as good simply because its elapsed time was below the threshold.

Azure queries must sum the counter values, represented by `AppMetrics.Sum`, rather than count exported rows or apply histogram `ItemCount` arithmetic to a counter.

Saborly confirmed that its counter rows have `ItemCount = 1` per export, with the value in `Sum`. That value is the increment for the export interval (delta temporality), not a running total: its rows read 1, 2, 4, then 1. A row's `Sum` is never the answer by itself, and taking the latest or the largest row undercounts. Totals for a window come from summing `Sum` across every row in it. Document this in the query recipes.

Queries must handle:

- No eligible traffic as no data, not 100% compliance.
- Traffic with zero within-threshold events as zero compliance.
- A single selected threshold, rather than double-counting across configured thresholds.
- Aborted requests according to an explicit eligibility policy.

The new counter does not replace the histogram or require a general `operation.count` instrument. It supports exact ratios at the configured thresholds, not arbitrary percentiles. Metrics are independent of trace sampling, but still subject to export failures, ingestion loss, and backend cardinality limits.

## TFR-30: Exclude endpoints before measurement starts

Add `[ExcludeFromServiceLevelIndicator]` for controllers and actions, plus an endpoint-builder extension:

```csharp
app.MapHealthChecks("/health")
    .ExcludeFromServiceLevelIndicator();

app.MapGroup("/internal")
    .ExcludeFromServiceLevelIndicator();
```

The same exclusion mechanism should work on individual Minimal API endpoints.

Rules:

- Exclusion wins over automatic emission and explicit SLI inclusion.
- Exclusion is inherited from controller or route-group metadata.
- The decision happens before starting a measurement, attaching the SLI feature, or running SLI enrichments.
- Excluded requests produce no SLI duration, threshold count, or measurement-related diagnostic count.
- Existing opt-in behavior remains unchanged for endpoints that are not excluded.

This excludes the endpoint from Trellis SLI instrumentation only. It does not disable ASP.NET Core or Application Insights request telemetry, change the endpoint response, or remove the value of a separately monitored synthetic availability check.

## SLO policies: do not silently reuse one population

Document these as distinct objectives:

| Objective | Good events | Eligible events |
| --- | --- | --- |
| Default availability | `Success` | `Success` and `Failure` |
| Successful-request latency | Successful operations within the threshold | All successful operations |
| Successful-and-fast service | Successful operations within the threshold | An explicitly defined valid population, potentially including aborted requests |

Successful-request latency should be considered alongside availability; excluding failed requests from that latency denominator must not hide their availability impact.

Do not change the library default to count every cancellation as a service failure. A user-facing objective may deliberately count aborted valid requests as misses, while another policy may exclude deliberate navigation cancellations. The service often needs additional information to distinguish the causes.

Do not implement "include aborts" by including all `Ignored` events. Use the distinct abort indicator and a stated policy.

The threshold counter records timing facts. Query recipes specify eligibility and good-event rules; the core does not decide an application's SLO target or error budget.

### Saborly's selected pilot policy

Saborly selected a successful-and-fast objective, evaluated separately at 2 s and 10 s. It cannot reliably distinguish deadline cancellations from deliberate caller cancellations, so it will include only aborts that already exceeded the selected threshold.

For one selected threshold and the same service/operation/customer/version scope:

```text
good =
    count(Outcome == Success and result == within)

eligible =
    count(Outcome in {Success, Failure})
    + count(Outcome == Ignored and aborted and result == exceeded)

compliance = good / eligible
```

The two eligible sets are disjoint. Use counter sums for these counts.

| Event | Good | Eligible |
| --- | --- | --- |
| Successful and within threshold | Yes | Yes |
| Successful but over threshold | No | Yes |
| Failure, whether fast or slow | No | Yes |
| Aborted and over threshold | No | Yes |
| Aborted and within threshold | No | No |
| Client error or another ignored event | No | No |

Exclude health and other out-of-scope operations before aggregation, including through endpoint exclusion when available. A zero eligible denominator is no data. Equality belongs to `within`, as defined by the recorded millisecond boundary.

This is an application-selected policy for observed events, not a new library default or a claim that early cancellations and unobserved end-to-end waits were satisfactory.

## Observations and resolution

### API-version date formatting

The enrichment currently uses the resolved `ApiVersion` string representation, and existing date-based coverage expects a date-only string.

Saborly's review confirms that the stored type is `datetime` for its date-based version and `string` for `Unspecified`. The reported boundary is Kusto's interpretation of date-like strings in the dynamic `Properties` column, not a library formatting defect. Saborly has retracted the original observation as a library issue.

No SLI formatting change is proposed. The following remains a useful diagnostic for the stored type:

```kusto
AppMetrics
| where TimeGenerated > ago(1d) and Name == "operation.duration"
| project ApiVersion = Properties["http.api.version"],
          ApiVersionType = gettype(Properties["http.api.version"])
| take 20
```

### Requests without a matched endpoint

Retain the current behavior and document it explicitly: automatic emission covers routed endpoints reached by the SLI middleware, not every incoming HTTP request.

A true route miss is not measured. A matched endpoint without a usable route template can be measured as `<METHOD> <unrouted>`. These are different cases.

Use platform or edge telemetry for the broader incoming-request population. Do not silently add scanner traffic or arbitrary raw paths to the operation-level SLI.

## Delivery plan

1. **Finalize the revised contract.** Implemented and documented: default-aware API, abort-first policy, counter name/unit/projection, threshold boundary, and pilot query semantics.
2. **Prepare a preview containing TFR-26, TFR-27, and TFR-29.** Implemented locally with MVC/Minimal API abort grouping, exception propagation, custom-meter behavior, and real mid-flight SqlClient/HttpClient cancellation fixtures. SQL coverage uses an isolated LocalDB instance and a Windows CI job; provider dependencies are test-only.
3. **Use the offered consumer validation.** Saborly has offered to deploy that preview to its test site and check Azure counter sums, abort metadata, and threshold ratios. Compare a known workload with a bucket-preserving backend as well, before presenting the feature as portable latency-SLO support.
4. **Deliver the remaining ergonomics independently.** TFR-30 endpoint exclusion can accompany the preview when ready; TFR-28 generic result selectors need not block it. Keep any Trellis-specific Results adapter separate and defer per-operation threshold configuration.

These are delivery phases, not promised release dates. Saborly's review accepts the overall direction and does not consider the listed concerns reasons to hold back a preview once the design addresses them.

### Acceptance criteria

| Area | Required behavior |
| --- | --- |
| Classification | A one-status override preserves all other built-in mappings; contextual quota/capacity decisions are possible; the existing callback continues to work. |
| Abort telemetry | No phantom 200 before response start; actual started status is retained; abort-first classification does not depend on the outer exception type; exceptions propagate. |
| Exception cases | Direct, wrapped, aggregate, and provider-specific exceptions during an observed abort remain `Ignored`; escaping exceptions without a request abort remain `Failure`. Real SqlClient and HttpClient mid-flight scenarios are covered through integration fixtures. |
| Abort diagnostic | An aborted request that ends in a plain cancellation records `sli.request.abort_exception = cancellation`; any other escaping exception, including wrapped provider cancellations, records `other`; no escaping exception, or no abort, omits it. It never changes `Outcome`, and it is absent from the threshold counter. |
| Grouping | Known operation/resource/version metadata remains usable for aborted requests without forcing arbitrary canceled async enrichment. |
| Result helpers (later) | Failure values emit the selected outcome; the original result is returned; action exceptions and cancellations retain their established behavior. |
| Threshold counts | Each operation contributes exactly once per configured threshold across all recording paths, including failures and cancellations; retained dimensions match their final duration values and both instruments use the configured meter. |
| Counter projection | Status and method remain on the duration histogram but are omitted from the counter; required dimensions, API version, abort indicator, and custom dimensions are retained when present. The documented bound is `2 * N * P`. |
| Threshold queries | Boundary, zero-good-event, no-traffic, and multiple-threshold cases have unambiguous results. Fast failures are misses, slow aborts are misses, fast aborts are excluded, and other ignored events do not enter the pilot denominator. |
| Exclusion (later) | Controller, action, Minimal API, group, and health-check exclusion work in automatic and opt-in modes without emitting SLI measurements or changing responses. |

## Review decisions and remaining coordination

The consumer review settles the original feedback points: the default-aware callback is sufficient; no per-status mapping API is wanted; the pilot uses global 2 s and 10 s thresholds with the slow-abort policy above; and the API-version observation is resolved at the Kusto boundary.

The counter and diagnostic contracts are settled, Azure delta-increment query guidance is documented, and provider fixtures are implemented without shipping provider dependencies.

Remaining rollout coordination is preview publication and Saborly's offered deployment: capture exported counter totals, preserved abort metadata, and pilot ratios; compare the known workload with a bucket-preserving backend. TFR-28/30, per-operation thresholds, and the Results adapter remain separate later work.

## References

- [Existing Trellis SLI metric-contract RFC](sli-metric-contract.md).
- Saborly, "Review of the SLI feature-request proposal", 2026-10-15 (`trellis-sli-proposal-review.md` in the consumer repository), including its second review of this revision.
- [Azure Monitor histogram and counter conversion source](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter/src/Customizations/Models/MetricDataPoint.cs).
- [Azure Monitor AppMetrics schema](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/tables/appmetrics).
- [OpenTelemetry HTTP metrics conventions](https://opentelemetry.io/docs/specs/semconv/http/http-metrics/), including response-status presence.
- [Kusto dynamic data type](https://learn.microsoft.com/en-us/kusto/query/scalar-data-types/dynamic), including strongly typed interpretation of parseable strings.
