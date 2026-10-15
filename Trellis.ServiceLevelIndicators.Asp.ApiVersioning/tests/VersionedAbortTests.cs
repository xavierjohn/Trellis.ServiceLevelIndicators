namespace Trellis.ServiceLevelIndicators.Asp.ApiVersioning.Tests;

using global::Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public sealed class VersionedAbortTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Aborted_requests_keep_resolved_version_and_customer_dimensions(
        bool mvc,
        bool versionInHeader,
        bool defaultVersion)
    {
        using var capture = new MetricCapture();
        var apiVersion = new ApiVersion(new DateOnly(2023, 8, 29));
        var requestCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddControllers();
                    services.AddApiVersioning(options =>
                    {
                        options.ApiVersionReader = ApiVersionReader.Combine(
                            new QueryStringApiVersionReader(),
                            new HeaderApiVersionReader("api-version"));
#pragma warning disable AV0016 // Verifies the existing default-version fallback on aborted requests.
                        options.AssumeDefaultVersionWhenUnspecified = defaultVersion;
#pragma warning restore AV0016
                        options.DefaultApiVersion = apiVersion;
                    }).AddMvc();
                    services.AddServiceLevelIndicator(options =>
                    {
                        options.Meter = capture.Meter;
                        options.CustomerResourceId = "tenant-default";
                        options.LocationId = "test-region";
                        options.LatencyThresholds.Add(TimeSpan.FromSeconds(2));
                        options.LatencyThresholds.Add(TimeSpan.FromSeconds(10));
                    }).AddMvc().AddApiVersion();
                })
                .Configure(app => app.Use(async (context, next) =>
                    {
                        try
                        {
                            await next(context);
                        }
                        finally
                        {
                            requestCompleted.TrySetResult();
                        }
                    })
                    .UseRouting()
                    .UseServiceLevelIndicator()
                    .UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();
                        var versionSet = endpoints.NewApiVersionSet().HasApiVersion(apiVersion).Build();
                        endpoints.MapGet("/minimal/{id}/abort", (HttpContext context) => AbortRequest(context))
                            .WithApiVersionSet(versionSet)
                            .MapToApiVersion(apiVersion)
                            .WithMetadata(new CustomerResourceIdMetadata("id"));
                    })))
            .StartAsync(TestContext.Current.CancellationToken);
        using var client = host.GetTestClient();
        var path = mvc ? "/TestSingle/abort/tenant-route" : "/minimal/tenant-route/abort";
        if (versionInHeader)
            client.DefaultRequestHeaders.Add("api-version", "2023-08-29");
        else if (!defaultVersion)
            path += "?api-version=2023-08-29";

        Func<Task> invoke = () => client.GetAsync(path, TestContext.Current.CancellationToken);
        await invoke.Should().ThrowAsync<OperationCanceledException>();
        await requestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["Outcome"].Should().Be("Ignored");
        duration.Tags["CustomerResourceId"].Should().Be("tenant-route");
        duration.Tags["http.api.version"].Should().Be("2023-08-29");
        duration.Tags["sli.request.aborted"].Should().Be(true);
        duration.Tags["sli.request.abort_exception"].Should().Be("cancellation");
        duration.Tags.Should().NotContainKey("http.response.status.code");
        duration.Tags["Operation"].Should().Be(mvc
            ? "GET TestSingle/abort/{id}"
            : "GET /minimal/{id}/abort");
        var counts = capture.For("operation.duration.threshold.count").ToArray();
        counts.Should().HaveCount(2);
        counts.Should().OnlyContain(count =>
            Equals(count.Tags["http.api.version"], "2023-08-29")
            && Equals(count.Tags["CustomerResourceId"], "tenant-route")
            && Equals(count.Tags["Outcome"], "Ignored")
            && Equals(count.Tags["sli.request.aborted"], true)
            && !count.Tags.ContainsKey("http.response.status.code")
            && !count.Tags.ContainsKey("sli.request.abort_exception")
            && !count.Tags.ContainsKey("http.request.method"));
    }

    private static IResult AbortRequest(HttpContext context)
    {
        context.Abort();
        throw new OperationCanceledException(context.RequestAborted);
    }
}
