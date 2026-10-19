# Trellis.ServiceLevelIndicators.Asp.ApiVersioning

[![NuGet Package](https://img.shields.io/nuget/v/Trellis.ServiceLevelIndicators.Asp.ApiVersioning.svg)](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp.ApiVersioning)

Adds **API version** as a metric dimension to [Trellis.ServiceLevelIndicators.Asp](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp) SLI metrics. Works with the [Asp.Versioning](https://github.com/dotnet/aspnet-api-versioning) package.

## When To Use This Package

Choose `Trellis.ServiceLevelIndicators.Asp.ApiVersioning` only when your ASP.NET Core application already uses [Asp.Versioning](https://github.com/dotnet/aspnet-api-versioning) and you want the resolved API version included in emitted SLI metrics.

This package extends [Trellis.ServiceLevelIndicators.Asp](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp); it is not a replacement for it.

## Installation

```shell
dotnet add package Trellis.ServiceLevelIndicators.Asp.ApiVersioning --prerelease
```

> Requires [Trellis.ServiceLevelIndicators.Asp](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp) (installed automatically as a dependency).

## Usage

Chain `AddApiVersion()` onto the SLI builder:

```csharp
builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
})
.AddMvc()
.AddApiVersion();
```

This registers `ApiVersionEnrichment`, which reads the resolved API version from the request and emits it as an attribute on every SLI measurement.

Already-resolved versions are retained on aborted MVC and Minimal API requests. The built-in enrichment opts into `IEnrichment<WebEnrichmentContext>.EnrichOnCancellation` because it performs bounded metadata access without I/O; arbitrary user async enrichments are still skipped after cancellation.

When `ServiceLevelIndicatorOptions.LatencyThresholds` enables `operation.duration.threshold.count`, the counter also retains `http.api.version`. Configure distinct positive whole-millisecond thresholds at startup, for example `options.LatencyThresholds.Add(TimeSpan.FromSeconds(2))`, and register the same SLI meter with OpenTelemetry.

## Requirements

- ASP.NET Core application using [Trellis.ServiceLevelIndicators.Asp](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp)
- Asp.Versioning configured in the application
- `AddApiVersion()` chained onto the SLI builder

## Emitted Attribute

| Attribute | Description |
|-----------|-------------|
| `http.api.version` | The single resolved API version string (e.g. `1.0`, `2024-01-15`), `Neutral` for API-version-neutral endpoints, `Unspecified` when no version is requested and no default is assumed, or an empty string for invalid or ambiguous requests |

This attribute is added alongside the standard dimensions emitted by `Trellis.ServiceLevelIndicators.Asp`. On aborts, the duration includes `sli.request.aborted = true` and omits `http.response.status.code` if headers have not started. The threshold counter omits HTTP status, method, and the abort-exception diagnostic.

Date-based versions remain date-only strings in library telemetry. Kusto may interpret a date-like value in dynamic `Properties` as a `datetime`; normalize it at the query boundary rather than changing the library's version format.

## Optional AgentDocs setup

This package includes a versioned API reference, but restoring it does not install
agent instructions. To opt in, restore your consuming project or solution, then
run from its Git root:

```powershell
dotnet new tool-manifest --output .config
dotnet tool install Trellis.AgentDocs --version 1.0.2 --tool-manifest .config/dotnet-tools.json
dotnet tool run agentdocs init <solution-or-project>
```

If the repository already has `.config/dotnet-tools.json`, reuse it instead of
creating another manifest. Restoring a package never activates its guide: `init`
lists `Trellis.ServiceLevelIndicators.Asp.ApiVersioning` as pending and prints the package IDs to add to
`approvedPackages` in `.agentdocs/policy.json`. Add the ones you trust, then run
`dotnet tool run agentdocs sync` to install the guides under Git-root
`.agentdocs/`. The guide is on-demand: the generated index describes it, and an
agent opens it when its task concerns SLI metrics. After a package upgrade, run
`dotnet restore --force` and then `dotnet tool run agentdocs sync`.

## Further Reading

- [Full documentation and samples](https://github.com/xavierjohn/Trellis.ServiceLevelIndicators)
- [Package selection and usage reference](https://github.com/xavierjohn/Trellis.ServiceLevelIndicators/blob/main/docs/usage-reference.md)
- [ASP.NET API Versioning](https://github.com/dotnet/aspnet-api-versioning)
