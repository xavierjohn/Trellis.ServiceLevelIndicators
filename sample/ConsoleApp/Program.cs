using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Trellis.ServiceLevelIndicators;

using var meter = new Meter("SampleConsoleSLI");
var services = new ServiceCollection();
services.AddServiceLevelIndicator(options =>
{
    options.Meter = meter;
    options.CustomerResourceId = "project-blue";
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.LatencyThresholds.Add(TimeSpan.FromMilliseconds(250));
});

using ServiceProvider serviceProvider = services.BuildServiceProvider();
using MeterProvider meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("SampleConsoleSLI"))
    .AddServiceLevelIndicatorInstrumentation(meter)
    .AddConsoleExporter()
    .Build();

ServiceLevelIndicator sli = serviceProvider.GetRequiredService<ServiceLevelIndicator>();
Console.WriteLine("Measuring SyncProject for project-blue. Metrics are exported at shutdown.");
await sli.MeasureAsync("SyncProject", () => Task.Delay(75));
