# Trellis.ServiceLevelIndicators.Asp.ApiVersioning

[![NuGet Package](https://img.shields.io/nuget/v/Trellis.ServiceLevelIndicators.Asp.ApiVersioning.svg)](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp.ApiVersioning)

Adds **API version** as a metric dimension to [Trellis.ServiceLevelIndicators.Asp](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp) SLI metrics. Works with the [Asp.Versioning](https://github.com/dotnet/aspnet-api-versioning) package.

## When To Use This Package

Choose `Trellis.ServiceLevelIndicators.Asp.ApiVersioning` only when your ASP.NET Core application already uses [Asp.Versioning](https://github.com/dotnet/aspnet-api-versioning) and you want the resolved API version included in emitted SLI metrics.

This package extends [Trellis.ServiceLevelIndicators.Asp](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp); it is not a replacement for it.

## Installation

```shell
dotnet add package Trellis.ServiceLevelIndicators.Asp.ApiVersioning
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

## Requirements

- ASP.NET Core application using [Trellis.ServiceLevelIndicators.Asp](https://www.nuget.org/packages/Trellis.ServiceLevelIndicators.Asp)
- Asp.Versioning configured in the application
- `AddApiVersion()` chained onto the SLI builder

## Emitted Attribute

| Attribute | Description |
|-----------|-------------|
| `http.api.version` | The single resolved API version string (e.g. `1.0`, `2024-01-15`), `Neutral` for API-version-neutral endpoints, `Unspecified` when no version is requested and no default is assumed, or an empty string for invalid or ambiguous requests |

This attribute is added alongside all the standard attributes emitted by `Trellis.ServiceLevelIndicators.Asp` (`Operation`, `CustomerResourceId`, `LocationId`, `Outcome`, `http.request.method`, `http.response.status.code`).

## Optional AgentDocs setup

This package includes a versioned API reference, but restoring it does not install
agent instructions. To opt in, restore your consuming project or solution, then
run from its Git root:

```powershell
dotnet new tool-manifest --output .config
dotnet tool install Trellis.AgentDocs --version 0.1.0-preview.19 --tool-manifest .config/dotnet-tools.json
dotnet tool run agentdocs init <solution-or-project>
```

If the repository already has `.config/dotnet-tools.json`, reuse it instead of
creating another manifest. Restoring a package never activates its guide: `init`
lists `Trellis.ServiceLevelIndicators.Asp.ApiVersioning` as pending and prints the package IDs to add to
`approvedPackages` in `.agentdocs/policy.json`. Add the ones you trust, then run
`dotnet tool run agentdocs sync` to install the guides under Git-root
`.agentdocs/`. The guide is on-demand: the generated index describes it, and an
agent opens it when its task concerns SLI metrics. After a package upgrade, run
`dotnet restore` and then `dotnet tool run agentdocs sync`.

## Further Reading

- [Full documentation and samples](https://github.com/xavierjohn/ServiceLevelIndicators)
- [Package selection and usage reference](https://github.com/xavierjohn/ServiceLevelIndicators/blob/main/docs/usage-reference.md)
- [ASP.NET API Versioning](https://github.com/dotnet/aspnet-api-versioning)
