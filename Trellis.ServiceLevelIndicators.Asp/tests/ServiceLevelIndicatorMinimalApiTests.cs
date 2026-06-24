namespace Trellis.ServiceLevelIndicators.Asp.Tests;

using System;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class ServiceLevelIndicatorMinimalApiTests : IDisposable
{
    private const int MillisecondsDelay = 200;
    private readonly Meter _meter;
    private readonly MeterListener _meterListener;
    private readonly ITestOutputHelper _output;
    private KeyValuePair<string, object?>[] _actualTags = [];
    private Instrument? _instrument;
    private long _measurement;
    private bool _callbackCalled;
    private bool _disposedValue;

    public ServiceLevelIndicatorMinimalApiTests(ITestOutputHelper output)
    {
        _output = output;
        const string MeterName = "SliMinApiTestMeter";
        _meter = new(MeterName, "1.0.0");
        _meterListener = new()
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name is MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<long>(OnMeasurementRecorded);
        _meterListener.Start();
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_for_minimal_api_get()
    {
        // Arrange
        using var host = await CreateMinimalApiHost();

        // Act
        var response = await host.GetTestClient().GetAsync("hello", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET /hello"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_with_custom_operation_name()
    {
        // Arrange
        using var host = await CreateMinimalApiHost();

        // Act
        var response = await host.GetTestClient().GetAsync("custom-operation", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "CustomOp"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_with_customer_resource_id_from_route()
    {
        // Arrange
        using var host = await CreateMinimalApiHost();

        // Act
        var response = await host.GetTestClient().GetAsync("resource/myResourceId", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "myResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET /resource/{id}"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_with_measure_attribute()
    {
        // Arrange
        using var host = await CreateMinimalApiHost();

        // Act
        var response = await host.GetTestClient().GetAsync("measured/items/Widget", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("name", "Widget"),
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET /measured/items/{name}"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_emits_route_template_not_concrete_path_for_minimal_api_with_route_param()
    {
        // Regression: route placeholders must be preserved in the Operation tag so cardinality stays bounded.
        // Arrange
        using var host = await CreateMinimalApiHost();

        // Act: hit the same endpoint twice with different route values.
        var operations = new List<string?>();
        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            _callbackCalled = true;
            _instrument = instrument;
            foreach (var t in tags.ToArray())
                if (t.Key == "Operation") operations.Add(t.Value?.ToString());
        });

        (await host.GetTestClient().GetAsync("resource/abc", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await host.GetTestClient().GetAsync("resource/xyz", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        // Assert: both requests collapse to the same bounded operation name.
        operations.Should().HaveCount(2);
        operations.Should().AllBe("GET /resource/{id}");
    }

    [Fact]
    public async Task SLI_Metrics_trims_trailing_slash_from_route_group_root_operation()
    {
        // Regression: a route group's root endpoint ("/grouped" prefix + "/") produces a trailing
        // slash in the raw route template. The Operation tag must drop it so "/grouped/" and
        // "/grouped" map to a single bounded series rather than two.
        // Arrange
        using var host = await CreateMinimalApiHost();

        // Act
        var response = await host.GetTestClient().PostAsync("grouped/", content: null, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "POST /grouped"),
            new("Outcome", "Success"),
            new("http.request.method", "POST"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_trims_trailing_slash_from_explicitly_authored_route()
    {
        // The trailing-slash trim is deliberate and not limited to route-group roots: an explicitly
        // authored "/explicit-trailing/" route emits "GET /explicit-trailing" as well.
        // Arrange
        using var host = await CreateMinimalApiHost();

        // Act
        var response = await host.GetTestClient().GetAsync("explicit-trailing/", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET /explicit-trailing"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_preserves_literal_root_path_operation()
    {
        // Guard: the trim must keep the literal root path "/" intact, not reduce it to the empty string.
        // Arrange
        using var host = await CreateMinimalApiHost();

        // Act
        var response = await host.GetTestClient().GetAsync("/", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET /"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_not_emitted_when_AddServiceLevelIndicator_not_called()
    {
        // Arrange
        using var host = await CreateMinimalApiHost();

        // Act
        var response = await host.GetTestClient().GetAsync("no-sli", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _callbackCalled.Should().BeFalse();
    }

    [Fact]
    public async Task SLI_Metrics_is_automatically_emitted_for_minimal_api_when_automatic_emission_is_enabled()
    {
        // Arrange
        using var host = await CreateMinimalApiHostWithAutomaticEmission();

        // Act
        var response = await host.GetTestClient().GetAsync("auto-sli", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET /auto-sli"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_with_enrichment_for_minimal_api()
    {
        // Arrange
        using var host = await CreateMinimalApiHostWithEnrichment();

        // Act
        var response = await host.GetTestClient().GetAsync("hello", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET /hello"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
            new("http.request.method", "GET"),
        };

        ValidateMetrics(expectedTags);
    }

    private async Task<IHost> CreateMinimalApiHost() =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddServiceLevelIndicator(options =>
                    {
                        options.Meter = _meter;
                        options.CustomerResourceId = "TestCustomerResourceId";
                        options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "West US 3");
                        options.AutomaticallyEmitted = false;
                    });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseServiceLevelIndicator();
                    app.Use(async (context, next) =>
                    {
                        await Task.Delay(MillisecondsDelay);
                        await next(context);
                    });
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/hello", () => "Hello World!")
                            .AddServiceLevelIndicator();

                        endpoints.MapGet("/custom-operation", () => "Custom!")
                            .AddServiceLevelIndicator("CustomOp");

                        endpoints.MapGet("/resource/{id}", ([CustomerResourceId] string id) => $"Resource {id}")
                            .AddServiceLevelIndicator();

                        endpoints.MapGet("/measured/items/{name}", ([Measure] string name) => $"Item {name}")
                            .AddServiceLevelIndicator();

                        endpoints.MapGet("/no-sli", () => "No SLI");

                        // A route group's root endpoint: the "/grouped" prefix combined with the
                        // "/" pattern yields the raw template "/grouped/" (with a trailing slash).
                        var grouped = endpoints.MapGroup("/grouped")
                            .AddServiceLevelIndicator();
                        grouped.MapPost("/", () => "Created");

                        // An explicitly authored trailing-slash route normalizes the same way as a
                        // route-group root — the trim is deliberate and applies to any route template.
                        endpoints.MapGet("/explicit-trailing/", () => "Trailing")
                            .AddServiceLevelIndicator();

                        // The literal root path must keep its single slash (it must not be trimmed away).
                        endpoints.MapGet("/", () => "Root")
                            .AddServiceLevelIndicator();
                    });
                }))
            .StartAsync();

    private async Task<IHost> CreateMinimalApiHostWithEnrichment() =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddServiceLevelIndicator(options =>
                    {
                        options.Meter = _meter;
                        options.CustomerResourceId = "TestCustomerResourceId";
                        options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "West US 3");
                        options.AutomaticallyEmitted = false;
                    })
                    .AddHttpMethod();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseServiceLevelIndicator();
                    app.Use(async (context, next) =>
                    {
                        await Task.Delay(MillisecondsDelay);
                        await next(context);
                    });
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/hello", () => "Hello World!")
                            .AddServiceLevelIndicator());
                }))
            .StartAsync();

    private async Task<IHost> CreateMinimalApiHostWithAutomaticEmission() =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddServiceLevelIndicator(options =>
                    {
                        options.Meter = _meter;
                        options.CustomerResourceId = "TestCustomerResourceId";
                        options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "West US 3");
                    });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseServiceLevelIndicator();
                    app.Use(async (context, next) =>
                    {
                        await Task.Delay(MillisecondsDelay);
                        await next(context);
                    });
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/auto-sli", () => "Auto SLI"));
                }))
            .StartAsync();

    private void OnMeasurementRecorded(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        _callbackCalled = true;
        _instrument = instrument;
        _measurement = measurement;
        _actualTags = tags.ToArray();
        _output.WriteLine($"Measurement {measurement}");
    }

    private void ValidateMetrics(KeyValuePair<string, object?>[] expectedTags)
    {
        expectedTags = AddDefaultHttpMethod(expectedTags);

        _callbackCalled.Should().BeTrue();
        _instrument!.Name.Should().Be("operation.duration");
        _instrument.Unit.Should().Be("ms");
        _measurement.Should().BeInRange(MillisecondsDelay - 10, MillisecondsDelay + 400);
        _actualTags.Should().NotContain(tag => tag.Key == "activity.status.code");
        _actualTags.Should().BeEquivalentTo(expectedTags);
    }

    private static KeyValuePair<string, object?>[] AddDefaultHttpMethod(KeyValuePair<string, object?>[] expectedTags)
    {
        if (expectedTags.Any(tag => tag.Key == "http.request.method"))
            return expectedTags;

        return [.. expectedTags, new KeyValuePair<string, object?>("http.request.method", "GET")];
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _meter.Dispose();
                _meterListener.Dispose();
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
