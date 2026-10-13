namespace Trellis.ServiceLevelIndicators.Asp.Tests;

using System;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class ServiceLevelIndicatorAspTests : IDisposable
{
    private readonly Meter _meter;
    private readonly MeterListener _meterListener;
    private readonly TaskCompletionSource<bool> _measurementRecorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ITestOutputHelper _output;
    private bool _callbackCalled;
    private bool _disposedValue;
    private KeyValuePair<string, object?>[] _actualTags = [];
    private Instrument? _instrument;
    private long _measurement;

    public ServiceLevelIndicatorAspTests(ITestOutputHelper output)
    {
        _output = output;
        const string MeterName = "SliTestMeter";
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
    public async Task SLI_Metrics_is_emitted_for_successful_API_call()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync("test", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_for_successful_POST_API_call()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().PostAsync("test", new StringContent("Hi"), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "POST Test"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_for_failed_API_call()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync("test/bad_request", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/bad_request"),
            new("Outcome", "ClientError"),
            new("http.response.status.code", 400),
        };

        ValidateMetrics(expectedTags);
    }

    [Theory]
    [InlineData("test/redirect", HttpStatusCode.Found, "GET Test/redirect", "Success", 302)]
    [InlineData("test/unprocessable", HttpStatusCode.UnprocessableEntity, "GET Test/unprocessable", "ClientError", 422)]
    [InlineData("test/too_many_requests", (HttpStatusCode)429, "GET Test/too_many_requests", "Failure", 429)]
    public async Task SLI_Metrics_classifies_http_status_codes(string route, HttpStatusCode expectedStatus, string operation, string outcome, int statusCode)
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync(route, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(expectedStatus);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", operation),
            new("Outcome", outcome),
            new("http.response.status.code", statusCode),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_uses_configured_http_outcome_classifier()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(
            _meter,
            context => context.Response.StatusCode == 429 ? SliOutcome.ClientError : SliOutcome.Failure);

        var response = await host.GetTestClient().GetAsync("test/too_many_requests", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be((HttpStatusCode)429);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/too_many_requests"),
            new("Outcome", "ClientError"),
            new("http.response.status.code", 429),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_with_enriched_data()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "test");
        request.Headers.Add("from", "xavier@somewhere.com");

        using var host = await TestHostBuilder.CreateHostWithSliEnriched(_meter);

        var response = await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "xavier@somewhere.com"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test"),
            new("Outcome", "Success"),
            new("http.request.method", "GET"),
            new("http.response.status.code", 200),
            new("foo", "bar"),
            new("test", "again"),
            new("enrichAsync", "async"),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task Override_Operation_name()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync("test/operation", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "TestOperation"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task Override_CustomerResourceId()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync("test/customer_resourceid/myId", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "myId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/customer_resourceid/{id}"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task CustomAttribute_is_added_to_SLI_dimension()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync("test/custom_attribute/Mickey", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/custom_attribute/{value}"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
            new("CustomAttribute", "Mickey"),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task When_automatically_emit_SLI_is_Off_do_not_send_SLI()
    {
        using var host = await TestHostBuilder.CreateHostWithoutAutomaticSli(_meter);

        var response = await host.GetTestClient().GetAsync("test", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _callbackCalled.Should().BeFalse();
    }

    [Fact]
    public async Task When_automatically_emit_SLI_is_Off_X2C_send_SLI_using_attribute()
    {
        using var host = await TestHostBuilder.CreateHostWithoutAutomaticSli(_meter);

        var response = await host.GetTestClient().GetAsync("test/send_sli", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/send_sli"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task GetMeasuredOperation_will_throw_if_route_does_not_emit_SLI()
    {
        using var host = await TestHostBuilder.CreateHostWithoutSli();

        var response = await host.GetTestClient().GetAsync("test", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        Func<Task> getMeasuredOperationLatency = () => host.GetTestClient().GetAsync("test/custom_attribute/Mickey", TestContext.Current.CancellationToken);

        await getMeasuredOperationLatency.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TryGetMeasuredOperation_will_return_false_if_route_does_not_emit_SLI()
    {
        using var host = await TestHostBuilder.CreateHostWithoutSli();

        var response = await host.GetTestClient().GetAsync("test/try_get_measured_operation/Donald", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        content.Should().Be("false");
    }

    [Fact]
    public async Task TryGetMeasuredOperation_will_return_true_if_route_emits_SLI()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync("test/try_get_measured_operation/Goofy", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        content.Should().Be("true");

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/try_get_measured_operation/{value}"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
            new("CustomAttribute", "Goofy"),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Measure_is_emitted()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync("test/name/Xavier/Jon/25", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "Jon"),
            new("first", "Xavier"),
            new("age", "25"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/name/{first}/{surname}/{age}"),
            new("Outcome", "Success"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_multiple_CustomerResourceId_MinimalApi_will_fail()
    {
        // Minimal API endpoint with multiple [CustomerResourceId] should throw when the endpoint is built
        using var host = await new HostBuilder()
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
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/bad/{a}/{b}",
                            ([CustomerResourceId] string a, [CustomerResourceId] string b) => a + b)
                        .AddServiceLevelIndicator());
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        Func<Task> act = () => host.GetTestClient().GetAsync("/bad/x/y", TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Multiple*CustomerResourceId*");
    }

    [Fact]
    public async Task SLI_multiple_CustomerResourceId_Mvc_will_fail()
    {
        // MVC controller action with multiple [CustomerResourceId] should throw at startup
        Func<Task> act = () => new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    var mvcBuilder = services.AddControllers();
                    mvcBuilder.PartManager.FeatureProviders.Add(
                        new SingleControllerFeatureProvider(typeof(MultipleCustomerResourceIdController)));
                    services.AddServiceLevelIndicator(options =>
                    {
                        options.Meter = _meter;
                        options.CustomerResourceId = "TestCustomerResourceId";
                        options.LocationId = ServiceLevelIndicator.CreateLocationId("public", "West US 3");
                    }).AddMvc();
                })
                .Configure(app => app.UseRouting()
                    .UseServiceLevelIndicator()
                    .UseEndpoints(endpoints => endpoints.MapControllers())))
            .StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Multiple*CustomerResourceId*");
    }

    [Fact]
    public async Task Middleware_should_not_emit_metrics_for_nonexistent_route()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync("does-not-exist", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        _callbackCalled.Should().BeFalse();
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_for_server_error()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        var response = await host.GetTestClient().GetAsync("test/server_error", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/server_error"),
            new("Outcome", "Failure"),
            new("http.response.status.code", 500),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_for_unhandled_exception()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        Func<Task> act = () => host.GetTestClient().GetAsync("test/throw", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Boom");

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/throw"),
            new("Outcome", "Failure"),
            new("http.response.status.code", 500),
        };

        ValidateMetrics(expectedTags);
    }

    [Fact]
    public async Task SLI_Metrics_is_emitted_as_ignored_for_request_aborted_cancellation()
    {
        using var host = await TestHostBuilder.CreateHostWithSli(_meter);

        Func<Task> act = () => host.GetTestClient().GetAsync("test/request_aborted", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _measurementRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var expectedTags = new KeyValuePair<string, object?>[]
        {
            new("CustomerResourceId", "TestCustomerResourceId"),
            new("LocationId", "ms-loc://az/public/West US 3"),
            new("Operation", "GET Test/request_aborted"),
            new("Outcome", "Ignored"),
            new("http.response.status.code", 200),
        };

        ValidateMetrics(expectedTags);
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

    private void OnMeasurementRecorded(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        _callbackCalled = true;
        _instrument = instrument;
        _measurement = measurement;
        _actualTags = tags.ToArray();
        _measurementRecorded.TrySetResult(true);
        _output.WriteLine($"Measurement {measurement}");
    }

    private void ValidateMetrics(KeyValuePair<string, object?>[] expectedTags)
    {
        expectedTags = AddDefaultHttpMethod(expectedTags);

        _callbackCalled.Should().BeTrue();
        _instrument!.Name.Should().Be("operation.duration");
        _instrument.Unit.Should().Be("ms");
        _measurement.Should().BeInRange(TestHostBuilder.MillisecondsDelay - 10, TestHostBuilder.MillisecondsDelay + 400);
        _actualTags.Should().NotContain(tag => tag.Key == "activity.status.code");
        _actualTags.Should().BeEquivalentTo(expectedTags);
    }

    private static KeyValuePair<string, object?>[] AddDefaultHttpMethod(KeyValuePair<string, object?>[] expectedTags)
    {
        if (expectedTags.Any(tag => tag.Key == "http.request.method"))
            return expectedTags;

        var operation = expectedTags.First(tag => tag.Key == "Operation").Value?.ToString();
        var method = operation?.StartsWith("POST ", StringComparison.Ordinal) == true ? "POST" : "GET";

        return [.. expectedTags, new KeyValuePair<string, object?>("http.request.method", method)];
    }

}
