using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SliProductionDemo;
using SliProductionDemo.Controllers;
using Trellis.ServiceLevelIndicators;

var healthCheck = args is ["--health-check"];
var builder = WebApplication.CreateBuilder(healthCheck ? [] : args);
if (healthCheck)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try
    {
        using var response = await client.GetAsync(DemoReadiness.GetUri(builder.Configuration));
        if (response.IsSuccessStatusCode)
            return 0;

        Console.Error.WriteLine($"Demo readiness check failed: HTTP {(int)response.StatusCode}.");
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"Demo readiness check failed: {ex.Message}");
    }
    catch (OperationCanceledException ex)
    {
        Console.Error.WriteLine($"Demo readiness check timed out: {ex.Message}");
    }
    catch (FormatException ex)
    {
        Console.Error.WriteLine($"Demo readiness configuration is invalid: {ex.Message}");
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Demo readiness configuration is invalid: {ex.Message}");
    }

    return 1;
}

builder.Services.AddControllers();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "ProjectTrackerSliDemo",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"))
    .WithMetrics(metrics =>
    {
        metrics.AddServiceLevelIndicatorInstrumentation();
        metrics.AddOtlpExporter();
    });

builder.Services.AddServiceLevelIndicator(options =>
{
    options.AutomaticallyEmitted = true;
    options.CustomerResourceId = ProjectsController.UnknownProjectId;
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.LatencyThresholds.Add(TimeSpan.FromMilliseconds(250));
})
.AddMvc();

var app = builder.Build();

app.UseServiceLevelIndicator();
app.MapGet(DemoReadiness.Path, () => Results.Ok(new { status = "Healthy" }))
    .ExcludeFromServiceLevelIndicator();
app.MapControllers();

await app.RunAsync();
return 0;
