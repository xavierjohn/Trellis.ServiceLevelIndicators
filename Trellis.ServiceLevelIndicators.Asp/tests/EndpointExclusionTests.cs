namespace Trellis.ServiceLevelIndicators.Asp.Tests;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public sealed class EndpointExclusionTests
{
    [Theory]
    [InlineData(true, "/exclusion/minimal")]
    [InlineData(false, "/exclusion/minimal")]
    [InlineData(true, "/exclusion/reversed")]
    [InlineData(false, "/exclusion/reversed")]
    [InlineData(true, "/exclusion/automatic-only")]
    [InlineData(false, "/exclusion/automatic-only")]
    [InlineData(true, "/exclusion/attributed-handler")]
    [InlineData(false, "/exclusion/attributed-handler")]
    [InlineData(true, "/exclusion/group/child")]
    [InlineData(false, "/exclusion/group/child")]
    [InlineData(true, "/exclusion/group/nested/child")]
    [InlineData(false, "/exclusion/group/nested/child")]
    [InlineData(true, "/exclusion/health")]
    [InlineData(false, "/exclusion/health")]
    [InlineData(true, "/exclusion/actions/excluded")]
    [InlineData(false, "/exclusion/actions/excluded")]
    [InlineData(true, "/exclusion/controller/excluded")]
    [InlineData(false, "/exclusion/controller/excluded")]
    [InlineData(true, "/exclusion/inherited-controller/excluded")]
    [InlineData(false, "/exclusion/inherited-controller/excluded")]
    [InlineData(true, "/exclusion/inherited-action/excluded")]
    [InlineData(false, "/exclusion/inherited-action/excluded")]
    public async Task Excluded_endpoints_skip_all_automatic_SLI_work(bool automatic, string path)
    {
        using var capture = new MetricCapture();
        var state = new RequestState();
        using var host = await CreateHost(capture, state, automatic);

        var response = await host.GetTestClient().GetAsync(path, TestContext.Current.CancellationToken);
        await state.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        state.FeatureAttached.Should().BeFalse();
        state.ClassifierCalls.Should().Be(0);
        state.EnrichmentCalls.Should().Be(0);
        capture.Measurements.Should().BeEmpty();
        if (path == "/exclusion/health")
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("Healthy");
    }

    [Theory]
    [InlineData(true, "/exclusion/included", true)]
    [InlineData(false, "/exclusion/included", true)]
    [InlineData(true, "/exclusion/actions/included", true)]
    [InlineData(false, "/exclusion/actions/included", true)]
    [InlineData(true, "/exclusion/unannotated", true)]
    [InlineData(false, "/exclusion/unannotated", false)]
    public async Task Non_excluded_endpoints_preserve_automatic_and_opt_in_behavior(
        bool automatic, string path, bool shouldMeasure)
    {
        using var capture = new MetricCapture();
        var state = new RequestState();
        using var host = await CreateHost(capture, state, automatic);

        var response = await host.GetTestClient().GetAsync(path, TestContext.Current.CancellationToken);
        await state.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        state.FeatureAttached.Should().Be(shouldMeasure);
        state.ClassifierCalls.Should().Be(shouldMeasure ? 1 : 0);
        state.EnrichmentCalls.Should().Be(shouldMeasure ? 1 : 0);
        if (shouldMeasure)
        {
            capture.For("operation.duration").Should().ContainSingle()
                .Which.Tags["Outcome"].Should().Be("Success");
            capture.For("operation.duration.threshold.count").Should().HaveCount(2);
            capture.For("sli.diagnostics.unknown_customer_resource_id").Should().ContainSingle();
        }
        else
        {
            capture.Measurements.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Exclusion_precedes_attribute_validation_and_preserves_exceptions_and_status(
        bool automatic, bool aborted)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, automatic);
        using var cancellation = new CancellationTokenSource();
        if (aborted)
            cancellation.Cancel();
        Exception original = aborted
            ? new OperationCanceledException("Original cancellation", cancellation.Token)
            : new InvalidOperationException("Original failure");
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Request.Method = "GET";
        context.Request.RouteValues["id"] = "value";
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(
                new ExcludeFromServiceLevelIndicatorAttribute(),
                new ServiceLevelIndicatorAttribute(),
                new MeasureMetadata("id", "Outcome")),
            "Excluded endpoint without a route template"));
        var middleware = HttpOutcomePreviewTests.CreateMiddleware(
            _ => throw original,
            sli,
            new ServiceLevelIndicatorHttpOptions
            {
                ClassifyOutcome = _ => throw new InvalidOperationException("Classifier must not run.")
            },
            [new UnexpectedEnrichment()]);

        var propagated = await Xunit.Record.ExceptionAsync(() => middleware.InvokeAsync(context));

        propagated.Should().BeSameAs(original);
        context.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        context.Features.Get<IServiceLevelIndicatorFeature>().Should().BeNull();
        capture.Measurements.Should().BeEmpty();
    }

    [Fact]
    public void Exclusion_extension_rejects_a_null_builder()
    {
        RouteHandlerBuilder builder = null!;

        Action configure = () => builder.ExcludeFromServiceLevelIndicator();

        configure.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("builder");
    }

    private static async Task<IHost> CreateHost(MetricCapture capture, RequestState state, bool automatic) =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(EndpointExclusionTests).Assembly);
                    services.AddHealthChecks();
                    services.AddServiceLevelIndicator(options =>
                    {
                        options.Meter = capture.Meter;
                        options.LocationId = "test-region";
                        options.AutomaticallyEmitted = automatic;
                        options.LatencyThresholds.Add(TimeSpan.FromSeconds(2));
                        options.LatencyThresholds.Add(TimeSpan.FromSeconds(10));
                    })
                    .AddMvc()
                    .ClassifyHttpOutcome((_, outcome) =>
                    {
                        state.ClassifierCalls++;
                        return outcome;
                    })
                    .Enrich(_ => state.EnrichmentCalls++);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        try
                        {
                            await next(context);
                        }
                        finally
                        {
                            state.Completed.TrySetResult();
                        }
                    });
                    app.UseServiceLevelIndicator();
                    app.Use(async (context, next) =>
                    {
                        state.FeatureAttached = context.TryGetMeasuredOperation(out _);
                        await next(context);
                    });
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/exclusion/minimal", () => "OK")
                            .AddServiceLevelIndicator()
                            .ExcludeFromServiceLevelIndicator();
                        endpoints.MapGet("/exclusion/reversed", () => "OK")
                            .ExcludeFromServiceLevelIndicator()
                            .AddServiceLevelIndicator();
                        endpoints.MapGet("/exclusion/automatic-only", () => "OK")
                            .ExcludeFromServiceLevelIndicator();
                        endpoints.MapGet("/exclusion/attributed-handler", AttributedHandler)
                            .AddServiceLevelIndicator();
                        endpoints.MapGet("/exclusion/included", () => "OK")
                            .AddServiceLevelIndicator();
                        endpoints.MapGet("/exclusion/unannotated", () => "OK");
                        var group = endpoints.MapGroup("/exclusion/group")
                            .ExcludeFromServiceLevelIndicator();
                        group.MapGet("/child", () => "OK").AddServiceLevelIndicator();
                        group.MapGroup("/nested").MapGet("/child", () => "OK")
                            .AddServiceLevelIndicator();
                        endpoints.MapHealthChecks("/exclusion/health")
                            .AddServiceLevelIndicator()
                            .ExcludeFromServiceLevelIndicator();
                        endpoints.MapControllers();
                    });
                }))
            .StartAsync();

    [ExcludeFromServiceLevelIndicator]
    private static string AttributedHandler() => "OK";

    private static ServiceLevelIndicator CreateSli(MetricCapture capture, bool automatic)
    {
        var options = new ServiceLevelIndicatorOptions
        {
            Meter = capture.Meter,
            LocationId = "test-region",
            AutomaticallyEmitted = automatic
        };
        options.LatencyThresholds.Add(TimeSpan.FromSeconds(2));
        return new ServiceLevelIndicator(Options.Create(options));
    }

    private sealed class RequestState
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FeatureAttached { get; set; }
        public int ClassifierCalls { get; set; }
        public int EnrichmentCalls { get; set; }
    }

    private sealed class UnexpectedEnrichment : IEnrichment<WebEnrichmentContext>
    {
        public bool EnrichOnCancellation => throw new InvalidOperationException("Capability must not be read.");

        public ValueTask EnrichAsync(WebEnrichmentContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Enrichment must not run.");
    }
}

[ApiController]
[Route("exclusion/actions")]
[ServiceLevelIndicator]
public sealed class ExclusionActionsController : ControllerBase
{
    [HttpGet("excluded")]
    [ExcludeFromServiceLevelIndicator]
    public IActionResult Excluded() => Ok();

    [HttpGet("included")]
    public IActionResult Included() => Ok();
}

[ApiController]
[Route("exclusion/controller")]
[ServiceLevelIndicator]
[ExcludeFromServiceLevelIndicator]
public sealed class ExclusionController : ControllerBase
{
    [HttpGet("excluded")]
    public IActionResult Excluded() => Ok();
}

[ExcludeFromServiceLevelIndicator]
public abstract class ExclusionControllerBase : ControllerBase
{
}

[ApiController]
[Route("exclusion/inherited-controller")]
[ServiceLevelIndicator]
public sealed class InheritedExclusionController : ExclusionControllerBase
{
    [HttpGet("excluded")]
    public IActionResult Excluded() => Ok();
}

public abstract class ExclusionActionControllerBase : ControllerBase
{
    [HttpGet("excluded")]
    [ExcludeFromServiceLevelIndicator]
    public virtual IActionResult Excluded() => Ok();
}

[ApiController]
[Route("exclusion/inherited-action")]
[ServiceLevelIndicator]
public sealed class InheritedActionExclusionController : ExclusionActionControllerBase
{
    public override IActionResult Excluded() => Ok();
}
