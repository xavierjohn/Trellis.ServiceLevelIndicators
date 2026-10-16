namespace SliProductionDemo.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SliProductionDemo;
using SliProductionDemo.Controllers;
using Trellis.ServiceLevelIndicators;

public sealed class DemoScenarioTests
{
    [Theory]
    [InlineData("read", new[] { 25, 35, 45, 55 })]
    [InlineData("healthy", new[] { 45, 60, 75, 90 })]
    [InlineData("slow", new[] { 550, 700, 850, 1_000 })]
    [InlineData("client-error", new[] { 25, 35, 45 })]
    [InlineData("throttled", new[] { 35, 50, 65 })]
    [InlineData("failure", new[] { 450, 600, 750 })]
    public void Each_operation_mode_uses_its_documented_delay_palette(string mode, int[] expectedValues)
    {
        for (var i = 0; i < 1_000; i++)
            expectedValues.Should().Contain(ProjectsController.NextDelay(mode));
    }

    [Theory]
    [InlineData("healthy", HttpStatusCode.OK, "Success", null)]
    [InlineData("slow", HttpStatusCode.OK, "Success", null)]
    [InlineData("client-error", HttpStatusCode.BadRequest, "ClientError", "The requested sync range is invalid.")]
    [InlineData("throttled", HttpStatusCode.TooManyRequests, "Failure", "Sync capacity is temporarily exhausted.")]
    [InlineData("failure", HttpStatusCode.ServiceUnavailable, "Failure", "The downstream sync service is unavailable.")]
    [InlineData("unknown-mode", HttpStatusCode.BadRequest, "ClientError", "Unknown demo mode.")]
    public async Task Sync_records_the_outcome_and_exact_latency_threshold(
        string mode, HttpStatusCode expectedStatus, string expectedOutcome, string? expectedError)
    {
        using var capture = new MetricCapture();
        using var application = new DemoApplicationFactory(capture);
        using var client = application.CreateClient();

        using var response = await client.PostAsync(
            $"/api/projects/project-red/sync?mode={mode}",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(expectedStatus);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        payload.GetProperty("projectId").GetString().Should().Be("project-red");
        payload.GetProperty("mode").GetString().Should().Be(mode);
        if (expectedError is null)
            payload.GetProperty("synchronized").GetBoolean().Should().BeTrue();
        else
            payload.GetProperty("error").GetString().Should().Be(expectedError);
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        var threshold = capture.For("operation.duration.threshold.count").Should().ContainSingle().Subject;
        duration.Tags["CustomerResourceId"].Should().Be("project-red");
        duration.Tags["Operation"].Should().Be("SyncProject");
        duration.Tags["Outcome"].Should().Be(expectedOutcome);
        threshold.Value.Should().Be(1);
        threshold.Tags["CustomerResourceId"].Should().Be("project-red");
        threshold.Tags["Operation"].Should().Be("SyncProject");
        threshold.Tags["Outcome"].Should().Be(expectedOutcome);
        threshold.Tags["sli.latency.threshold_ms"].Should().Be(250L);
        if (mode is "slow" or "failure")
            threshold.Tags["sli.latency.result"].Should().Be("exceeded");
        threshold.Tags.Should().NotContainKeys("http.request.method", "http.response.status.code");
    }

    [Theory]
    [InlineData("project-blue")]
    [InlineData("project-green")]
    [InlineData("project-red")]
    public async Task Known_projects_retain_their_resource_dimension(string projectId)
    {
        using var capture = new MetricCapture();
        using var application = new DemoApplicationFactory(capture);
        using var client = application.CreateClient();

        using var response = await client.GetAsync(
            $"/api/projects/{projectId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["CustomerResourceId"].Should().Be(projectId);
        duration.Tags["Operation"].Should().Be("GetProject");
        duration.Tags["Outcome"].Should().Be("Success");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_projects_share_one_bounded_resource_dimension(bool synchronize)
    {
        using var capture = new MetricCapture();
        using var application = new DemoApplicationFactory(capture);
        using var client = application.CreateClient();

        foreach (var projectId in new[] { "unknown-project-one", "unknown-project-two" })
        {
            using var response = synchronize
                ? await client.PostAsync(
                    $"/api/projects/{projectId}/sync",
                    content: null,
                    cancellationToken: TestContext.Current.CancellationToken)
                : await client.GetAsync(
                    $"/api/projects/{projectId}", TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var durations = capture.For("operation.duration");
        var thresholds = capture.For("operation.duration.threshold.count");
        durations.Should().HaveCount(2);
        thresholds.Should().HaveCount(2);
        foreach (var measurement in durations.Concat(thresholds))
        {
            measurement.Tags["CustomerResourceId"].Should().Be("project-unknown");
            measurement.Tags["Outcome"].Should().Be("ClientError");
        }
    }

    [Fact]
    public async Task Readiness_is_excluded_from_sli_measurement()
    {
        using var capture = new MetricCapture();
        using var application = new DemoApplicationFactory(capture);
        using var client = application.CreateClient();

        application.Services.GetRequiredService<IOptions<ServiceLevelIndicatorOptions>>()
            .Value.AutomaticallyEmitted.Should().BeTrue();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capture.Measurements.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0L, SliOutcome.Success, "within")]
    [InlineData(90L, SliOutcome.Success, "within")]
    [InlineData(65L, SliOutcome.Failure, "within")]
    [InlineData(45L, SliOutcome.ClientError, "within")]
    [InlineData(250L, SliOutcome.Success, "within")]
    [InlineData(251L, SliOutcome.Success, "exceeded")]
    [InlineData(450L, SliOutcome.Failure, "exceeded")]
    public void Demo_threshold_counts_both_sides_of_250_ms_without_wall_clock_assumptions(
        long elapsedMilliseconds, SliOutcome outcome, string expectedResult)
    {
        using var capture = new MetricCapture();
        using var application = new DemoApplicationFactory(capture);
        var sli = application.Services.GetRequiredService<ServiceLevelIndicator>();

        sli.Record("SyncProject", "project-red", elapsedMilliseconds, outcome);

        var threshold = capture.For("operation.duration.threshold.count").Should().ContainSingle().Subject;
        threshold.Value.Should().Be(1);
        threshold.Tags["Operation"].Should().Be("SyncProject");
        threshold.Tags["CustomerResourceId"].Should().Be("project-red");
        threshold.Tags["Outcome"].Should().Be(outcome.ToString());
        threshold.Tags["sli.latency.threshold_ms"].Should().Be(250L);
        threshold.Tags["sli.latency.result"].Should().Be(expectedResult);
    }

    [Theory]
    [InlineData("http://+:8097", "8080", "http://localhost:8097/health")]
    [InlineData("http://*:8098", null, "http://localhost:8098/health")]
    [InlineData("http://0.0.0.0:8099", null, "http://localhost:8099/health")]
    [InlineData("http://[::]:8100", null, "http://localhost:8100/health")]
    [InlineData("http://127.0.0.1:8101", null, "http://127.0.0.1:8101/health")]
    [InlineData("http://localhost:8102;http://+:8103", null, "http://localhost:8102/health")]
    [InlineData("https://localhost:8443;http://+:8104", null, "http://localhost:8104/health")]
    [InlineData(null, "8105;8106", "http://localhost:8105/health")]
    [InlineData(null, null, "http://localhost:5000/health")]
    public void Readiness_uses_the_configured_http_listener_and_shared_route(
        string? urls, string? httpPorts, string expectedUri)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHostDefaults.ServerUrlsKey] = urls,
            [WebHostDefaults.HttpPortsKey] = httpPorts,
        }).Build();

        DemoReadiness.GetUri(configuration).Should().Be(new Uri(expectedUri));
    }

    [Fact]
    public void Readiness_rejects_a_configuration_without_a_probeable_http_listener()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHostDefaults.ServerUrlsKey] = "https://localhost:8443",
        }).Build();

        var act = () => DemoReadiness.GetUri(configuration);
        act.Should().Throw<InvalidOperationException>().WithMessage("*requires an HTTP listener*");
    }

    private sealed class DemoApplicationFactory(MetricCapture capture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(services =>
                services.Configure<ServiceLevelIndicatorOptions>(options => options.Meter = capture.Meter));
    }
}
