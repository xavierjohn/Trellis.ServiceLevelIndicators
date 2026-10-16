using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Scalar.AspNetCore;
using Trellis.ServiceLevelIndicators;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("SampleMinimalApiSli"))
    .WithMetrics(metrics =>
    {
        metrics.AddServiceLevelIndicatorInstrumentation();
        metrics.AddOtlpExporter();
    });

builder.Services.AddServiceLevelIndicator(options =>
{
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.AutomaticallyEmitted = false;
    options.LatencyThresholds.Add(TimeSpan.FromMilliseconds(250));
});

var app = builder.Build();
app.UseServiceLevelIndicator();

var projects = app.MapGroup("/projects/{projectId:regex(^project-(blue|green|red)$)}");

projects.MapGet("/", ([CustomerResourceId] string projectId) => Results.Ok(new { projectId }))
    .AddServiceLevelIndicator("GetProject");

projects.MapPost("/sync/{mode:regex(^(quick|standard|slow)$)}",
    async ([CustomerResourceId] string projectId, [Measure("SyncMode")] string mode,
        CancellationToken cancellationToken) =>
    {
        var delay = mode switch
        {
            "quick" => 50,
            "standard" => 150,
            "slow" => 400,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        await Task.Delay(delay, cancellationToken);
        return Results.Ok(new { projectId, mode });
    })
    .AddServiceLevelIndicator("SyncProject");

app.MapGet("/health", () => Results.Ok());

app.MapOpenApi();
app.MapScalarApiReference();
app.Run();

/// <summary>
/// Application entry point for integration tests.
/// </summary>
public partial class Program;
