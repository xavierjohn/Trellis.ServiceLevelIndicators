extern alias minimal;
extern alias versioned;

namespace FocusedSamples.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Trellis.ServiceLevelIndicators;

public sealed class FocusedSampleTests
{
    [Theory]
    [InlineData("project-blue")]
    [InlineData("project-green")]
    [InlineData("project-red")]
    public async Task Minimal_api_records_the_named_operation_and_target_resource(string projectId)
    {
        using var capture = new MetricCapture();
        using var application = new SampleApplicationFactory<minimal::Program>(capture);
        using var client = application.CreateClient();

        using var response = await client.GetAsync(
            $"/projects/{projectId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["CustomerResourceId"].Should().Be(projectId);
        duration.Tags["Operation"].Should().Be("GetProject");
        duration.Tags["Outcome"].Should().Be("Success");
    }

    [Theory]
    [InlineData("quick")]
    [InlineData("standard")]
    [InlineData("slow")]
    public async Task Minimal_api_records_bounded_modes_and_exact_threshold_counts(string mode)
    {
        using var capture = new MetricCapture();
        using var application = new SampleApplicationFactory<minimal::Program>(capture);
        using var client = application.CreateClient();

        using var response = await client.PostAsync(
            $"/projects/project-red/sync/{mode}",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        var threshold = capture.For("operation.duration.threshold.count").Should().ContainSingle().Subject;
        duration.Tags["Operation"].Should().Be("SyncProject");
        duration.Tags["CustomerResourceId"].Should().Be("project-red");
        duration.Tags["SyncMode"].Should().Be(mode);
        duration.Tags["Outcome"].Should().Be("Success");
        threshold.Value.Should().Be(1);
        threshold.Tags["CustomerResourceId"].Should().Be("project-red");
        threshold.Tags["SyncMode"].Should().Be(mode);
        threshold.Tags["sli.latency.threshold_ms"].Should().Be(250L);
        if (mode == "slow")
            threshold.Tags["sli.latency.result"].Should().Be("exceeded");
    }

    [Theory]
    [InlineData("/health", HttpStatusCode.OK)]
    [InlineData("/openapi/v1.json", HttpStatusCode.OK)]
    [InlineData("/scalar/v1", HttpStatusCode.OK)]
    [InlineData("/projects/arbitrary-rejected-id", HttpStatusCode.NotFound)]
    public async Task Minimal_api_does_not_measure_health_docs_or_rejected_resources(
        string path, HttpStatusCode expectedStatus)
    {
        using var capture = new MetricCapture();
        using var application = new SampleApplicationFactory<minimal::Program>(capture);
        using var client = application.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(expectedStatus);
        capture.Measurements.Should().BeEmpty();
    }

    [Fact]
    public async Task Minimal_api_rejects_unbounded_sync_modes_without_measuring_them()
    {
        using var capture = new MetricCapture();
        using var application = new SampleApplicationFactory<minimal::Program>(capture);
        using var client = application.CreateClient();

        using var response = await client.PostAsync(
            "/projects/project-red/sync/arbitrary-mode",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        capture.Measurements.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("2.0")]
    public async Task Versioned_mvc_retains_the_same_operation_and_resource_across_versions(string version)
    {
        using var capture = new MetricCapture();
        using var application = new SampleApplicationFactory<versioned::Program>(capture);
        using var client = application.CreateClient();

        using var response = await client.GetAsync(
            $"/projects/project-blue?api-version={version}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var project = await response.Content.ReadFromJsonAsync<ProjectResponse>(TestContext.Current.CancellationToken);
        project.Should().Be(new ProjectResponse("project-blue", version));
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        var threshold = capture.For("operation.duration.threshold.count").Should().ContainSingle().Subject;
        duration.Tags["Operation"].Should().Be("GetProject");
        duration.Tags["CustomerResourceId"].Should().Be("project-blue");
        duration.Tags["Outcome"].Should().Be("Success");
        duration.Tags["http.api.version"].Should().Be(version);
        threshold.Tags["http.api.version"].Should().Be(version);
    }

    [Theory]
    [InlineData("/health", HttpStatusCode.OK)]
    [InlineData("/openapi/v1.json", HttpStatusCode.OK)]
    [InlineData("/openapi/v2.json", HttpStatusCode.OK)]
    [InlineData("/scalar/v2", HttpStatusCode.OK)]
    [InlineData("/projects/arbitrary-rejected-id?api-version=1.0", HttpStatusCode.NotFound)]
    public async Task Versioned_mvc_does_not_measure_health_docs_or_rejected_resources(
        string path, HttpStatusCode expectedStatus)
    {
        using var capture = new MetricCapture();
        using var application = new SampleApplicationFactory<versioned::Program>(capture);
        using var client = application.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(expectedStatus);
        capture.Measurements.Should().BeEmpty();
    }

    private sealed record ProjectResponse(string ProjectId, string ApiVersion);

    private sealed class SampleApplicationFactory<TEntryPoint>(MetricCapture capture)
        : WebApplicationFactory<TEntryPoint> where TEntryPoint : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
                services.Configure<ServiceLevelIndicatorOptions>(options => options.Meter = capture.Meter));
        }
    }
}
