using Asp.Versioning;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Scalar.AspNetCore;
using Trellis.ServiceLevelIndicators;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddApiVersioning(options => options.ApiVersionReader = new QueryStringApiVersionReader())
        .AddMvc()
        .AddApiExplorer(options => options.GroupNameFormat = "'v'VVV")
        .AddOpenApi(options => options.Document.AddScalarTransformers());

builder.Services.AddProblemDetails();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("SampleVersionedWebApplicationSLI"))
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
})
.AddMvc()
.AddApiVersion();

var app = builder.Build();
app.UseServiceLevelIndicator();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().WithDocumentPerVersion();
    app.MapScalarApiReference(options =>
    {
        var descriptions = app.DescribeApiVersions();
        for (var i = 0; i < descriptions.Count; i++)
        {
            var description = descriptions[i];
            var isDefault = i == descriptions.Count - 1;
            options.AddDocument(description.GroupName, description.GroupName, isDefault: isDefault);
        }
    });
}

app.MapControllers();
app.MapGet("/health", () => Results.Ok());
app.Run();

/// <summary>
/// Application entry point for integration tests.
/// </summary>
public partial class Program;
