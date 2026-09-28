using Asp.Versioning;
using Azure.Core;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Scalar.AspNetCore;
using Trellis.ServiceLevelIndicators;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddApiVersioning(options => options.ApiVersionReader = new QueryStringApiVersionReader())
        .AddMvc()
        .AddApiExplorer()
        .AddOpenApi(options => options.Document.AddScalarTransformers());

builder.Services.AddProblemDetails();

Action<ResourceBuilder> configureResource = r => r.AddService(
    serviceName: "SampleVersionedWebApplicationSLI",
    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
builder.Services.AddOpenTelemetry()
    .ConfigureResource(configureResource)
    .WithMetrics(builder =>
    {
        builder.AddServiceLevelIndicatorInstrumentation();
        builder.AddOtlpExporter();
    });

builder.Services.AddServiceLevelIndicator(options =>
{
    options.CustomerResourceId = "SampleVersionedApiResource";
    options.LocationId = ServiceLevelIndicator.CreateLocationId("public", AzureLocation.WestUS3.Name);
})
.AddMvc()
.AddApiVersion();

var app = builder.Build();

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

// Random delay.
Random rnd = new Random();
app.Use(async (context, next) =>
{
    await Task.Delay(rnd.Next(40, 200));
    await next(context);
});
app.UseHttpsRedirection();
app.UseServiceLevelIndicator();
app.UseAuthorization();

app.MapControllers();

app.Run();
