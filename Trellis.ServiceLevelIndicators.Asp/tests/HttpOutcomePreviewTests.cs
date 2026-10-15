namespace Trellis.ServiceLevelIndicators.Asp.Tests;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class HttpOutcomePreviewTests
{
    [Theory]
    [InlineData(100, SliOutcome.Ignored)]
    [InlineData(199, SliOutcome.Ignored)]
    [InlineData(200, SliOutcome.Success)]
    [InlineData(299, SliOutcome.Success)]
    [InlineData(302, SliOutcome.Success)]
    [InlineData(399, SliOutcome.Success)]
    [InlineData(400, SliOutcome.ClientError)]
    [InlineData(401, SliOutcome.ClientError)]
    [InlineData(402, SliOutcome.Ignored)]
    [InlineData(403, SliOutcome.ClientError)]
    [InlineData(404, SliOutcome.ClientError)]
    [InlineData(405, SliOutcome.Ignored)]
    [InlineData(409, SliOutcome.ClientError)]
    [InlineData(412, SliOutcome.ClientError)]
    [InlineData(422, SliOutcome.ClientError)]
    [InlineData(429, SliOutcome.Failure)]
    [InlineData(499, SliOutcome.Ignored)]
    [InlineData(500, SliOutcome.Failure)]
    [InlineData(503, SliOutcome.Failure)]
    [InlineData(599, SliOutcome.Failure)]
    public void Public_mapper_preserves_the_default_status_mapping(int statusCode, SliOutcome expected) =>
        HttpOutcomeClassifier.ClassifyStatusCode(statusCode).Should().Be(expected);

    [Theory]
    [InlineData(200, "Success")]
    [InlineData(302, "Success")]
    [InlineData(400, "ClientError")]
    [InlineData(429, "ClientError")]
    [InlineData(503, "Failure")]
    public async Task Default_aware_classifier_changes_one_status_without_replacing_other_mappings(int statusCode, string expected)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var services = new ServiceCollection();
        services.AddServiceLevelIndicator(options => options.LocationId = "test-region")
            .ClassifyHttpOutcome((context, defaultOutcome) =>
                context.Response.StatusCode == 429 ? SliOutcome.ClientError : defaultOutcome);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ServiceLevelIndicatorHttpOptions>>().Value;
        var context = CreateContext(TestContext.Current.CancellationToken);
        var middleware = CreateMiddleware(
            httpContext =>
            {
                httpContext.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            },
            sli,
            options);

        await middleware.InvokeAsync(context);

        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["Outcome"].Should().Be(expected);
        duration.Tags["http.response.status.code"].Should().Be(statusCode);
        capture.For("operation.duration.threshold.count").Should().ContainSingle()
            .Which.Tags["Outcome"].Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Last_classifier_registration_wins_across_overloads(bool defaultAwareLast)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var services = new ServiceCollection();
        var builder = services.AddServiceLevelIndicator(options => options.LocationId = "test-region");
        if (defaultAwareLast)
        {
            builder.ClassifyHttpOutcome(_ => SliOutcome.Failure)
                .ClassifyHttpOutcome((_, defaultOutcome) => defaultOutcome);
        }
        else
        {
            builder.ClassifyHttpOutcome((_, _) => SliOutcome.Failure)
                .ClassifyHttpOutcome(_ => SliOutcome.ClientError);
        }

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ServiceLevelIndicatorHttpOptions>>().Value;
        var middleware = CreateMiddleware(_ => Task.CompletedTask, sli, options);

        await middleware.InvokeAsync(CreateContext(TestContext.Current.CancellationToken));

        capture.For("operation.duration").Should().ContainSingle()
            .Which.Tags["Outcome"].Should().Be(defaultAwareLast ? "Success" : "ClientError");
    }

    [Fact]
    public async Task Default_aware_options_take_precedence_when_both_properties_are_set_directly()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var options = new ServiceLevelIndicatorHttpOptions
        {
            ClassifyOutcome = _ => throw new InvalidOperationException("The legacy callback must not run."),
            ClassifyOutcomeWithDefault = (_, defaultOutcome) => defaultOutcome
        };
        var middleware = CreateMiddleware(_ => Task.CompletedTask, sli, options);

        await middleware.InvokeAsync(CreateContext(TestContext.Current.CancellationToken));

        capture.For("operation.duration").Should().ContainSingle()
            .Which.Tags["Outcome"].Should().Be("Success");
    }

    [Fact]
    public void Default_aware_registration_rejects_null_arguments()
    {
        Action nullBuilder = () => ServiceLevelIndicatorServiceCollectionExtensions.ClassifyHttpOutcome(
            null!, (_, defaultOutcome) => defaultOutcome);
        var builder = new ServiceCollection().AddServiceLevelIndicator(options => options.LocationId = "test-region");
        Func<HttpContext, SliOutcome, SliOutcome> classifier = null!;
        Action nullClassifier = () => builder.ClassifyHttpOutcome(classifier);

        nullBuilder.Should().Throw<ArgumentNullException>().WithParameterName("builder");
        nullClassifier.Should().Throw<ArgumentNullException>().WithParameterName("classifier");
    }

    [Theory]
    [InlineData("cancellation", "cancellation")]
    [InlineData("task-cancellation", "cancellation")]
    [InlineData("http-wrapper", "other")]
    [InlineData("aggregate", "other")]
    [InlineData("independent", "other")]
    public async Task Abort_first_outcome_does_not_depend_on_exception_shape(string exceptionKind, string diagnostic)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        using var abort = new CancellationTokenSource();
        var context = CreateContext(abort.Token);
        var exception = CreateException(exceptionKind);
        var classifierCalls = 0;
        var middleware = CreateMiddleware(
            _ =>
            {
                abort.Cancel();
                throw exception;
            },
            sli,
            new ServiceLevelIndicatorHttpOptions
            {
                ClassifyOutcomeWithDefault = (_, _) =>
                {
                    classifierCalls++;
                    return SliOutcome.Success;
                }
            });

        Func<Task> invoke = () => middleware.InvokeAsync(context);
        var propagated = await Xunit.Record.ExceptionAsync(invoke);
        propagated.Should().BeSameAs(exception);

        classifierCalls.Should().Be(0);
        context.Response.StatusCode.Should().Be(200);
        context.Features.Get<IServiceLevelIndicatorFeature>().Should().BeNull();
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["Outcome"].Should().Be("Ignored");
        duration.Tags["Operation"].Should().Be("GET /items/{id}");
        duration.Tags["CustomerResourceId"].Should().Be("tenant-route");
        duration.Tags["sli.request.aborted"].Should().Be(true);
        duration.Tags["sli.request.abort_exception"].Should().Be(diagnostic);
        duration.Tags.Should().NotContainKey("http.response.status.code");
        var count = capture.For("operation.duration.threshold.count").Should().ContainSingle().Subject;
        count.Tags["sli.request.aborted"].Should().Be(true);
        count.Tags["Outcome"].Should().Be("Ignored");
        count.Tags.Should().NotContainKeys("http.request.method", "http.response.status.code", "sli.request.abort_exception");
    }

    [Theory]
    [InlineData("cancellation")]
    [InlineData("http-wrapper")]
    [InlineData("aggregate")]
    [InlineData("independent")]
    public async Task Exceptions_without_a_request_abort_remain_failures(string exceptionKind)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var context = CreateContext(TestContext.Current.CancellationToken);
        var exception = CreateException(exceptionKind);
        var middleware = CreateMiddleware(_ => throw exception, sli, new ServiceLevelIndicatorHttpOptions
        {
            ClassifyOutcome = _ => SliOutcome.Success
        });

        Func<Task> invoke = () => middleware.InvokeAsync(context);
        var propagated = await Xunit.Record.ExceptionAsync(invoke);
        propagated.Should().BeSameAs(exception);

        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["Outcome"].Should().Be("Failure");
        duration.Tags["http.response.status.code"].Should().Be(500);
        duration.Tags.Should().NotContainKeys("sli.request.aborted", "sli.request.abort_exception");
        context.Response.StatusCode.Should().Be(500);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abort_without_an_exception_omits_status_and_exception_diagnostic(bool defaultAwareClassifier)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        using var abort = new CancellationTokenSource();
        var classifierCalls = 0;
        Func<HttpContext, SliOutcome> classifier = _ =>
        {
            classifierCalls++;
            return SliOutcome.Success;
        };
        var options = new ServiceLevelIndicatorHttpOptions
        {
            ClassifyOutcome = defaultAwareClassifier ? null : classifier,
            ClassifyOutcomeWithDefault = defaultAwareClassifier ? (context, _) => classifier(context) : null
        };
        var middleware = CreateMiddleware(
            _ =>
            {
                abort.Cancel();
                return Task.CompletedTask;
            },
            sli,
            options);

        await middleware.InvokeAsync(CreateContext(abort.Token));

        classifierCalls.Should().Be(0);
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["Outcome"].Should().Be("Ignored");
        duration.Tags["sli.request.aborted"].Should().Be(true);
        duration.Tags.Should().NotContainKeys("http.response.status.code", "sli.request.abort_exception");
    }

    [Fact]
    public async Task Abort_after_headers_started_preserves_the_actual_status()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        using var abort = new CancellationTokenSource();
        var context = CreateContext(abort.Token);
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { StatusCode = 202 });
        var middleware = CreateMiddleware(
            _ =>
            {
                abort.Cancel();
                throw new HttpRequestException("wrapped cancellation", new TaskCanceledException());
            },
            sli);

        Func<Task> invoke = () => middleware.InvokeAsync(context);
        await invoke.Should().ThrowAsync<HttpRequestException>();

        context.Response.StatusCode.Should().Be(202);
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["http.response.status.code"].Should().Be(202);
        duration.Tags["Outcome"].Should().Be("Ignored");
        duration.Tags["sli.request.abort_exception"].Should().Be("other");
    }

    [Fact]
    public async Task Cancellation_safe_metadata_runs_even_after_skipped_user_enrichments()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        using var abort = new CancellationTokenSource();
        var unsafeEnrichment = new UnsafeEnrichment();
        var metadataEnrichment = new MetadataEnrichment();
        var middleware = CreateMiddleware(
            _ =>
            {
                abort.Cancel();
                return Task.CompletedTask;
            },
            sli,
            enrichments: [unsafeEnrichment, metadataEnrichment]);

        await middleware.InvokeAsync(CreateContext(abort.Token));

        unsafeEnrichment.Calls.Should().Be(0);
        metadataEnrichment.SawCancellation.Should().BeTrue();
        capture.For("operation.duration").Should().ContainSingle()
            .Which.Tags["http.api.version"].Should().Be("2026-07-04");
        capture.For("operation.duration.threshold.count").Should().ContainSingle()
            .Which.Tags["http.api.version"].Should().Be("2026-07-04");
    }

    [Fact]
    public async Task Enrichment_capability_errors_do_not_replace_request_exceptions_or_skip_later_metadata()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        using var abort = new CancellationTokenSource();
        var failedEnrichment = new FailingCancellationCapability();
        var metadataEnrichment = new MetadataEnrichment();
        var originalException = new HttpRequestException("dependency cancellation", new TaskCanceledException());
        var middleware = CreateMiddleware(
            _ =>
            {
                abort.Cancel();
                throw originalException;
            },
            sli,
            enrichments: [failedEnrichment, metadataEnrichment]);

        var propagated = await Xunit.Record.ExceptionAsync(
            () => middleware.InvokeAsync(CreateContext(abort.Token)));

        propagated.Should().BeSameAs(originalException);
        failedEnrichment.Calls.Should().Be(0);
        metadataEnrichment.SawCancellation.Should().BeTrue();
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["Outcome"].Should().Be("Ignored");
        duration.Tags["http.api.version"].Should().Be("2026-07-04");
        duration.Tags["sli.request.abort_exception"].Should().Be("other");
    }

    internal static ServiceLevelIndicator CreateSli(MetricCapture capture)
    {
        var options = new ServiceLevelIndicatorOptions
        {
            Meter = capture.Meter,
            CustomerResourceId = "tenant-default",
            LocationId = "test-region"
        };
        options.LatencyThresholds.Add(TimeSpan.FromSeconds(2));
        return new ServiceLevelIndicator(Options.Create(options));
    }

    internal static DefaultHttpContext CreateContext(CancellationToken cancellationToken = default)
    {
        var context = new DefaultHttpContext { RequestAborted = cancellationToken };
        context.Request.Method = "GET";
        context.Request.RouteValues["id"] = "tenant-route";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/items/{id}"),
            0,
            new EndpointMetadataCollection(new CustomerResourceIdMetadata("id")),
            "preview"));
        return context;
    }

    internal static ServiceLevelIndicatorMiddleware CreateMiddleware(
        RequestDelegate next,
        ServiceLevelIndicator sli,
        ServiceLevelIndicatorHttpOptions? options = null,
        IEnumerable<IEnrichment<WebEnrichmentContext>>? enrichments = null) =>
        new(next, sli, enrichments ?? [], NullLogger<ServiceLevelIndicatorMiddleware>.Instance,
            Options.Create(options ?? new ServiceLevelIndicatorHttpOptions()));

    private static Exception CreateException(string kind) => kind switch
    {
        "cancellation" => new OperationCanceledException(),
        "task-cancellation" => new TaskCanceledException(),
        "http-wrapper" => new HttpRequestException("wrapped cancellation", new TaskCanceledException()),
        "aggregate" => new AggregateException(new OperationCanceledException(), new InvalidOperationException("independent")),
        "independent" => new InvalidOperationException("independent"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class UnsafeEnrichment : IEnrichment<WebEnrichmentContext>
    {
        public int Calls { get; private set; }

        public ValueTask EnrichAsync(WebEnrichmentContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingCancellationCapability : IEnrichment<WebEnrichmentContext>
    {
        public bool EnrichOnCancellation => throw new InvalidOperationException("Invalid enrichment configuration.");
        public int Calls { get; private set; }

        public ValueTask EnrichAsync(WebEnrichmentContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MetadataEnrichment : IEnrichment<WebEnrichmentContext>
    {
        public bool EnrichOnCancellation => true;
        public bool SawCancellation { get; private set; }

        public ValueTask EnrichAsync(WebEnrichmentContext context, CancellationToken cancellationToken)
        {
            SawCancellation = cancellationToken.IsCancellationRequested;
            context.AddAttribute("http.api.version", "2026-07-04");
            return ValueTask.CompletedTask;
        }
    }
}
