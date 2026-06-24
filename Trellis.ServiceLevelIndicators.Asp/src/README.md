# Trellis.ServiceLevelIndicators.Asp

[![NuGet Package](https://img.shields.io/nuget/v/Trellis.ServiceLevelIndicators.Asp.svg)](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp)

ASP.NET Core middleware that **automatically emits Service Level Indicator (SLI) latency metrics in milliseconds** for every API operation. Built on the [Trellis.ServiceLevelIndicators](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators) core library, it helps teams move from generic request timing to service-specific latency metrics with dimensions such as operation, customer, location, and status.

For API versioning support, add [Trellis.ServiceLevelIndicators.Asp.ApiVersioning](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp.ApiVersioning).

## When To Use This Package

Choose `Trellis.ServiceLevelIndicators.Asp` when you are building an ASP.NET Core application and want SLI metrics emitted automatically for MVC controllers or Minimal API endpoints.

Use [Trellis.ServiceLevelIndicators](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators) if you only need manual measurement in non-HTTP code. Add [Trellis.ServiceLevelIndicators.Asp.ApiVersioning](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp.ApiVersioning) when your ASP.NET Core app uses Asp.Versioning and you want the resolved API version emitted as a metric dimension.

## Installation

```shell
dotnet add package Trellis.ServiceLevelIndicators.Asp
```

## Quick Start — MVC Controllers

```csharp
// 1. Register with OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddServiceLevelIndicatorInstrumentation();
        metrics.AddOtlpExporter();
    });

// 2. Configure SLI — AddMvc() enables attribute-based overrides
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
})
.AddMvc();

// 3. Add the middleware
app.UseServiceLevelIndicator();
```

## Quick Start — Minimal APIs

```csharp
// Register with OpenTelemetry as shown in the MVC example above.

builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
});

app.UseServiceLevelIndicator();

// Automatic emission is enabled by default. If you set
// options.AutomaticallyEmitted = false, mark individual endpoints:
app.MapGet("/hello", () => "Hello World!")
    .AddServiceLevelIndicator();
```

If you configure a custom `Meter` in `ServiceLevelIndicatorOptions`, register that same meter with OpenTelemetry by calling `AddServiceLevelIndicatorInstrumentation(meter)`.

`ServiceLevelIndicator` is a sealed `IDisposable` registered as a singleton; the DI container disposes it (and the `Meter` it created) at host shutdown — no manual cleanup needed. A `Meter` you supply via `Options.Meter` is owned by you and is never disposed by SLI.

Place `UseServiceLevelIndicator()` after routing and before endpoint execution so the middleware can read endpoint metadata and measure request handling.

## Emitted Metrics

By default, a meter named `Trellis.SLI` emits the `operation.duration` histogram in milliseconds. If you configure `ServiceLevelIndicatorOptions.Meter`, ASP.NET Core SLI metrics are emitted from that meter instead.

Measured HTTP requests emit the following attributes:

| Attribute | Description |
|-----------|-------------|
| `Operation` | The HTTP method + route template (e.g. `GET /teams/{teamId}`) — see [How `Operation` is resolved](#how-operation-is-resolved) below. |
| `CustomerResourceId` | The **target resource** of the operation — see [What `CustomerResourceId` is — and what it is NOT](#what-customerresourceid-is--and-what-it-is-not) below. |
| `LocationId` | Where the service is running |
| `Outcome` | `Success`, `Failure`, `ClientError`, or `Ignored`; 2xx/3xx responses are `Success`, common 4xx caller errors are `ClientError`, 429/5xx and unhandled exceptions are `Failure`, and request-aborted cancellations are `Ignored` |
| `http.response.status.code` | The HTTP response status code |
| `http.request.method` | The HTTP method |

### What `CustomerResourceId` is — and what it is NOT

`CustomerResourceId` is the **target resource of the operation** — the noun in the URL path being read or modified — normalized to a stable identifier. It lets you slice SLO compliance per tenant / customer / resource.

For `GET /teams/{teamId}` made by user `xa1` for team `team1`:

- ✅ `CustomerResourceId = "team1"` (the resource being acted on)
- ❌ NOT `"xa1"` — that's the *caller*; put it in `AddAttribute("CallerTier", ...)` or your audit log
- ❌ NOT a fresh `Guid.NewGuid()` per request — that explodes metric cardinality
- ❌ NOT the raw path `"/teams/team1"` — already covered by `Operation`

Wire it by decorating the route parameter that names the target resource:

```csharp
// MVC
[HttpGet("teams/{teamId}")]
[ServiceLevelIndicator(Operation = "GetTeam")]
public IActionResult GetTeam([CustomerResourceId] string teamId) => Ok();

// Minimal API
app.MapGet("/teams/{teamId}",
    ([CustomerResourceId] string teamId) => Results.Ok())
   .AddServiceLevelIndicator("GetTeam");
```

Or set it imperatively from claims/headers via `Enrich` or `HttpContext.GetMeasuredOperation()` — but the value must still be stable and meaningful.

### How `Operation` is resolved

`Operation` is resolved in this order:

1. `[ServiceLevelIndicator(Operation = "...")]` attribute (or `.AddServiceLevelIndicator("op")` for Minimal APIs).
2. MVC attribute route template (`AttributeRouteInfo.Template`).
3. The endpoint's route pattern (`RouteEndpoint.RoutePattern.RawText`) — covers Minimal APIs and conventional MVC routing. Placeholders such as `{id}` are preserved, **never** substituted with the concrete request value.

A trailing slash is trimmed from the resolved template (the literal root `/` is preserved), so a route group's root endpoint — where the `/orders` group prefix combines with the `/` pattern to produce `/orders/` — shares one series with `/orders` instead of emitting a separate `POST /orders/`.

If none of those yield a bounded template (e.g. a synthetic problem-details endpoint emitted by Asp.Versioning when the API version is invalid), the middleware emits the sentinel `"<METHOD> <unrouted>"` and logs a one-time warning per endpoint name. **If you see `<unrouted>` in your metrics, an endpoint is missing a route template — fix it by adding an attribute route or moving to a routed endpoint.**

## Customizations

### HTTP method dimension

`http.request.method` is emitted by default. `AddHttpMethod()` remains available as a no-op for older setup code.

### Enrich with custom data

Use `Enrich` (or `EnrichAsync`) to set `CustomerResourceId` from a stable identifier on the request (a tenant/subscription claim, a header, etc.) or to add custom attributes. **Do not use the caller's identity (UPN, user ID) as `CustomerResourceId`** — that's the caller, not the resource being acted on. See [What `CustomerResourceId` is — and what it is NOT](#what-customerresourceid-is--and-what-it-is-not).

```csharp
builder.Services.AddServiceLevelIndicator(options => { /* ... */ })
    .AddMvc()
    .Enrich(context =>
    {
        var tenantId = context.HttpContext.User.Claims
            .FirstOrDefault(c => c.Type == "tid")?.Value ?? "unknown";
        context.SetCustomerResourceId(tenantId);

        // Caller identity belongs in a separate (still bounded) dimension, never in CustomerResourceId.
        var tier = context.HttpContext.User.Claims
            .FirstOrDefault(c => c.Type == "tier")?.Value ?? "free";
        context.AddAttribute("CallerTier", tier);
    });
```

### Override the operation name

```csharp
[HttpGet("MyAction")]
[ServiceLevelIndicator(Operation = "MyCustomName")]
public IActionResult Get() => Ok();
```

### Set CustomerResourceId from a route parameter

```csharp
[HttpGet("teams/{teamId}")]
public IActionResult GetTeam([CustomerResourceId] string teamId) => Ok();
```

Or imperatively:

```csharp
HttpContext.GetMeasuredOperation().CustomerResourceId = customerResourceId;
```

### Add custom attributes from route parameters

Parameters decorated with `[Measure]` are automatically added as metric dimensions:

```csharp
[HttpGet("name/{first}/{teamId}")]
public IActionResult Get([Measure] string first, [CustomerResourceId] string teamId) => Ok();
```

### Add custom attributes manually

```csharp
HttpContext.GetMeasuredOperation().AddAttribute("CustomKey", value);

// Safe version for middleware (won't throw if SLI is not configured for the route)
if (HttpContext.TryGetMeasuredOperation(out var op))
    op.AddAttribute("CustomKey", value);
```

## Cardinality Guidance

Required tags must be stable and meaningful:

- **`Operation`** is bounded for you by the route-template resolver above (one series per HTTP method × route template). Watch your metrics for the `<unrouted>` sentinel — it means an endpoint is missing a route template.
- **`LocationId`** is set once per process from configuration — naturally bounded.
- **`CustomerResourceId`** is your responsibility. Use a stable tenant / subscription / resource identifier; do not use per-request GUIDs, timestamps, request IDs, or raw user input. High-cardinality customer resources are acceptable when they are stable, meaningful, and supported by your metrics backend.

The same discipline applies to `[Measure]` parameters and any custom attributes you add via `AddAttribute(...)`.

### Opt-in mode

To disable automatic SLI emission on all controllers:

```csharp
options.AutomaticallyEmitted = false;
```

Then add `[ServiceLevelIndicator]` only to the controllers you want measured.

## Further Reading

- [Full documentation and samples](https://github.com/xavierjohn/ServiceLevelIndicators)
- [Package selection and usage reference](https://github.com/xavierjohn/ServiceLevelIndicators/blob/main/docs/usage-reference.md)
