namespace SliProductionDemo.Tests;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
    [InlineData("healthy", HttpStatusCode.OK, "Success")]
    [InlineData("slow", HttpStatusCode.OK, "Success")]
    [InlineData("client-error", HttpStatusCode.BadRequest, "ClientError")]
    [InlineData("throttled", HttpStatusCode.TooManyRequests, "Failure")]
    [InlineData("failure", HttpStatusCode.ServiceUnavailable, "Failure")]
    [InlineData("unknown-mode", HttpStatusCode.BadRequest, "ClientError")]
    public async Task Sync_records_the_outcome_and_exact_latency_threshold(
        string mode, HttpStatusCode expectedStatus, string expectedOutcome)
    {
        using var capture = new MetricCapture();
        using var application = new DemoApplicationFactory(capture);
        using var client = application.CreateClient();

        using var response = await client.PostAsync(
            $"/api/projects/project-red/sync?mode={mode}",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(expectedStatus);
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

    private sealed class DemoApplicationFactory(MetricCapture capture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(services =>
                services.Configure<ServiceLevelIndicatorOptions>(options => options.Meter = capture.Meter));
    }
}
