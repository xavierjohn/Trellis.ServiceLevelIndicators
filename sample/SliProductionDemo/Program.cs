using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SliProductionDemo.Controllers;
using Trellis.ServiceLevelIndicators;

var builder = WebApplication.CreateBuilder(args);

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
    options.CustomerResourceId = ProjectsController.UnknownProjectId;
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "westus3");
    options.LatencyThresholds.Add(TimeSpan.FromMilliseconds(250));
})
.AddMvc();

var app = builder.Build();

app.UseServiceLevelIndicator();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }))
    .ExcludeFromServiceLevelIndicator();
app.MapControllers();

app.Run();
