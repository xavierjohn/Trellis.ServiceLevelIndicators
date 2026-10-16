namespace Trellis.ServiceLevelIndicators.Asp.Tests;

using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class InstrumentationHardeningTests
{
    [Theory]
    [InlineData(false, false, 200, "Success")]
    [InlineData(true, false, 200, "Success")]
    [InlineData(false, true, 200, "Success")]
    [InlineData(true, true, 200, "Success")]
    [InlineData(false, false, 401, "ClientError")]
    [InlineData(true, false, 401, "ClientError")]
    [InlineData(false, true, 401, "ClientError")]
    [InlineData(true, true, 401, "ClientError")]
    [InlineData(false, false, 500, "Failure")]
    [InlineData(true, false, 500, "Failure")]
    [InlineData(false, true, 500, "Failure")]
    [InlineData(true, true, 500, "Failure")]
    [InlineData(false, false, 429, "Failure")]
    [InlineData(true, false, 429, "Failure")]
    [InlineData(false, true, 429, "Failure")]
    [InlineData(true, true, 429, "Failure")]
    public async Task Classifier_faults_use_the_default_log_and_continue_enrichment(
        bool defaultAware, bool undefined, int statusCode, string expectedOutcome)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        var context = CreateContext(name, TestContext.Current.CancellationToken);
        context.Response.StatusCode = statusCode;
        var logger = new DiagnosticTestLogger<ServiceLevelIndicatorMiddleware>();
        var original = new InvalidOperationException("Classifier failed");
        var calls = 0;
        var enrichmentCalls = 0;
        SliOutcome Classify(HttpContext _)
        {
            calls++;
            return undefined ? (SliOutcome)99 : throw original;
        }

        var options = CreateOptions(defaultAware, Classify);
        var middleware = CreateMiddleware(_ => Task.CompletedTask, sli, logger, options,
            [new TestEnrichment(enrichment =>
            {
                enrichmentCalls++;
                context.TryGetMeasuredOperation(out _).Should().BeTrue();
                enrichment.AddAttribute("Tier", "standard");
            })]);

        await middleware.InvokeAsync(context);

        calls.Should().Be(1);
        enrichmentCalls.Should().Be(1);
        context.Response.StatusCode.Should().Be(statusCode);
        context.TryGetMeasuredOperation(out _).Should().BeFalse();
        AssertOutcome(capture, expectedOutcome);
        capture.Measurements.Should().OnlyContain(measurement => Equals(measurement.Tags["Tier"], "standard"));
        var log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Message.Should().Contain(expectedOutcome);
        if (undefined)
            log.Exception.Should().BeOfType<ArgumentOutOfRangeException>();
        else
            log.Exception.Should().BeSameAs(original);
        var failure = diagnostics.Failures.Should().ContainSingle().Subject;
        failure.Name.Should().Be("HttpOutcomeClassificationFailed");
        failure.Data["defaultOutcome"].Should().Be(expectedOutcome);
        failure.Data["error"].Should().BeOfType<string>().Which.Should()
            .Contain(undefined ? nameof(ArgumentOutOfRangeException) : original.Message);
    }

    [Theory]
    [InlineData(true, true, 200, "Success")]
    [InlineData(false, true, 200, "Success")]
    [InlineData(true, true, 429, "Failure")]
    [InlineData(false, true, 429, "Failure")]
    [InlineData(true, false, 202, "Success")]
    [InlineData(false, false, 202, "Success")]
    public async Task Classifier_and_enrichment_failures_are_observable_with_noop_or_filtered_logging(
        bool nullLogger, bool classifierFailure, int statusCode, string expectedOutcome)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        var context = CreateContext(name, TestContext.Current.CancellationToken);
        context.Response.StatusCode = statusCode;
        var filteredLogger = new DiagnosticTestLogger<ServiceLevelIndicatorMiddleware>(enabled: false);
        ILogger<ServiceLevelIndicatorMiddleware> logger = nullLogger
            ? NullLogger<ServiceLevelIndicatorMiddleware>.Instance
            : filteredLogger;
        var laterCalls = 0;
        IEnrichment<WebEnrichmentContext>[] enrichments = classifierFailure
            ? [new TestEnrichment(_ => laterCalls++)]
            :
            [
                new TestEnrichment(_ => throw new InvalidOperationException("Enrichment failed")),
                new TestEnrichment(_ => laterCalls++)
            ];
        var middleware = CreateMiddleware(_ => Task.CompletedTask, sli, logger,
            classifierFailure
                ? CreateOptions(false, _ => throw new InvalidOperationException("Classifier failed"))
                : null,
            enrichments);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(statusCode);
        laterCalls.Should().Be(1);
        filteredLogger.Entries.Should().BeEmpty();
        AssertOutcome(capture, expectedOutcome);
        var failure = diagnostics.Failures.Should().ContainSingle().Subject;
        failure.Name.Should().Be(classifierFailure ? "HttpOutcomeClassificationFailed" : "EnrichmentFailed");
        if (classifierFailure)
            failure.Data["defaultOutcome"].Should().Be(expectedOutcome);
        else
            failure.Data["enrichmentType"].Should().Be(nameof(TestEnrichment));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Healthy_classifiers_and_enrichments_emit_no_failure_diagnostics(bool defaultAware)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        var middleware = CreateMiddleware(_ => Task.CompletedTask, sli,
            NullLogger<ServiceLevelIndicatorMiddleware>.Instance,
            CreateOptions(defaultAware, _ => SliOutcome.ClientError),
            [new TestEnrichment(enrichment => enrichment.AddAttribute("Tier", "standard"))]);

        await middleware.InvokeAsync(CreateContext(name, TestContext.Current.CancellationToken));

        AssertOutcome(capture, "ClientError");
        diagnostics.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Default_aware_classifier_failure_does_not_fall_through_to_the_legacy_callback()
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        var logger = new DiagnosticTestLogger<ServiceLevelIndicatorMiddleware>();
        var legacyCalls = 0;
        var options = new ServiceLevelIndicatorHttpOptions
        {
            ClassifyOutcomeWithDefault = (_, _) => throw new InvalidOperationException("Classifier failed"),
            ClassifyOutcome = _ => { legacyCalls++; return SliOutcome.Failure; }
        };

        await CreateMiddleware(_ => Task.CompletedTask, sli, logger, options)
            .InvokeAsync(CreateContext(UniqueOperation(), TestContext.Current.CancellationToken));

        legacyCalls.Should().Be(0);
        AssertOutcome(capture, "Success");
        logger.Entries.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Classifier_cancellation_without_a_request_abort_is_a_logged_classifier_failure(bool defaultAware)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        var logger = new DiagnosticTestLogger<ServiceLevelIndicatorMiddleware>();
        var options = CreateOptions(defaultAware, _ => throw new OperationCanceledException("Classifier canceled"));

        await CreateMiddleware(_ => Task.CompletedTask, sli, logger, options)
            .InvokeAsync(CreateContext(UniqueOperation(), TestContext.Current.CancellationToken));

        AssertOutcome(capture, "Success");
        logger.Entries.Should().ContainSingle().Which.Exception.Should().BeOfType<OperationCanceledException>();
    }

    [Theory]
    [InlineData(false, "IsEnabled")]
    [InlineData(false, "Log")]
    [InlineData(true, "IsEnabled")]
    [InlineData(true, "Log")]
    public async Task Broken_enrichment_logging_preserves_the_original_exception_and_later_enrichments(
        bool aborted, string throwFrom)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        using var abort = new CancellationTokenSource();
        if (aborted)
            abort.Cancel();
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        var context = CreateContext(name, abort.Token);
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        var original = new InvalidOperationException("Original handler failure");
        var laterEnrichmentCalls = 0;
        var classifierCalls = 0;
        var middleware = CreateMiddleware(_ => throw original, sli,
            new DiagnosticTestLogger<ServiceLevelIndicatorMiddleware>(throwFrom),
            CreateOptions(true, _ => { classifierCalls++; return SliOutcome.Success; }),
            [
                new TestEnrichment(_ => throw new InvalidOperationException("Enrichment failed")),
                new TestEnrichment(enrichment =>
                {
                    laterEnrichmentCalls++;
                    enrichment.AddAttribute("Tier", "standard");
                })
            ]);

        var propagated = await Xunit.Record.ExceptionAsync(() => middleware.InvokeAsync(context));

        propagated.Should().BeSameAs(original);
        laterEnrichmentCalls.Should().Be(1);
        classifierCalls.Should().Be(0);
        context.TryGetMeasuredOperation(out _).Should().BeFalse();
        AssertOutcome(capture, aborted ? "Ignored" : "Failure");
        diagnostics.Failures.Select(failure => failure.Name).Should()
            .BeEquivalentTo("EnrichmentFailed", "DiagnosticLoggingFailed");
        var failure = diagnostics.Failures.Should()
            .ContainSingle(failure => failure.Name == "DiagnosticLoggingFailed").Subject;
        failure.Data["stage"].Should().Be("enrichment");
        failure.Data["originalError"].Should().BeOfType<string>().Which.Should().Contain("Enrichment failed");
        failure.Data["loggingError"].Should().BeOfType<string>().Which.Should().Contain("Broken logger");
    }

    [Theory]
    [InlineData("classification", "IsEnabled")]
    [InlineData("classification", "Log")]
    [InlineData("enrichment", "IsEnabled")]
    [InlineData("enrichment", "Log")]
    [InlineData("route", "IsEnabled")]
    [InlineData("route", "Log")]
    public async Task Broken_logging_never_breaks_an_otherwise_completed_request(string stage, string throwFrom)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        var name = stage == "route" ? "GET <unrouted>" : UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        var context = CreateContext(name, TestContext.Current.CancellationToken);
        if (stage == "route")
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new ServiceLevelIndicatorAttribute()), "Unrouted test endpoint"));
        var handlerCalls = 0;
        var middleware = CreateMiddleware(_ =>
        {
            handlerCalls++;
            return Task.CompletedTask;
        }, sli, new DiagnosticTestLogger<ServiceLevelIndicatorMiddleware>(throwFrom),
            stage == "classification"
                ? CreateOptions(false, _ => throw new InvalidOperationException("Classifier failed"))
                : null,
            stage == "enrichment"
                ? [new TestEnrichment(_ => throw new InvalidOperationException("Enrichment failed"))]
                : null);

        await middleware.InvokeAsync(context);

        handlerCalls.Should().Be(1);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.TryGetMeasuredOperation(out _).Should().BeFalse();
        AssertOutcome(capture, "Success");
        string[] expectedDiagnostics = stage switch
        {
            "classification" => ["HttpOutcomeClassificationFailed", "DiagnosticLoggingFailed"],
            "enrichment" => ["EnrichmentFailed", "DiagnosticLoggingFailed"],
            _ => ["DiagnosticLoggingFailed"]
        };
        diagnostics.Failures.Select(failure => failure.Name).Should().BeEquivalentTo(expectedDiagnostics);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Invalid_raw_attributes_never_mask_handler_failures_or_change_responses(bool handlerFails, bool aborted)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        using var abort = new CancellationTokenSource();
        if (aborted)
            abort.Cancel();
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        var context = CreateContext(name, abort.Token);
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        var original = new InvalidOperationException("Original handler failure");
        var middleware = CreateMiddleware(httpContext =>
        {
            httpContext.GetMeasuredOperation().Attributes.Add(new("Outcome", "invalid"));
            return handlerFails ? throw original : Task.CompletedTask;
        }, sli, new DiagnosticTestLogger<ServiceLevelIndicatorMiddleware>());

        var propagated = await Xunit.Record.ExceptionAsync(() => middleware.InvokeAsync(context));

        if (handlerFails)
            propagated.Should().BeSameAs(original);
        else
            propagated.Should().BeNull();
        context.Response.StatusCode.Should().Be(handlerFails && !aborted ? 500 : 202);
        context.TryGetMeasuredOperation(out _).Should().BeFalse();
        capture.Measurements.Should().BeEmpty();
        diagnostics.Failures.Should().ContainSingle().Which.Name.Should().Be("MeasurementRecordingFailed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_metric_provider_never_masks_the_original_handler_exception(bool aborted)
    {
        using var capture = new MetricCapture();
        using var sli = HttpOutcomePreviewTests.CreateSli(capture);
        using var abort = new CancellationTokenSource();
        if (aborted)
            abort.Cancel();
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, capture.Meter) && instrument.Name == "operation.duration")
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("Provider failed"));
        listener.Start();
        var original = new InvalidOperationException("Original handler failure");
        var context = CreateContext(name, abort.Token);

        var propagated = await Xunit.Record.ExceptionAsync(() =>
            CreateMiddleware(_ => throw original, sli, new DiagnosticTestLogger<ServiceLevelIndicatorMiddleware>())
                .InvokeAsync(context));

        propagated.Should().BeSameAs(original);
        context.TryGetMeasuredOperation(out _).Should().BeFalse();
        capture.For("operation.duration.threshold.count").Should().BeEmpty();
        diagnostics.Failures.Should().ContainSingle().Which.Name.Should().Be("MeasurementRecordingFailed");
    }

    private static ServiceLevelIndicatorHttpOptions CreateOptions(
        bool defaultAware, Func<HttpContext, SliOutcome> classifier) => new()
        {
            ClassifyOutcome = defaultAware ? null : classifier,
            ClassifyOutcomeWithDefault = defaultAware ? (context, _) => classifier(context) : null
        };

    private static DefaultHttpContext CreateContext(string operation, CancellationToken cancellationToken = default)
    {
        var context = HttpOutcomePreviewTests.CreateContext(cancellationToken);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ServiceLevelIndicatorAttribute(operation)), operation));
        return context;
    }

    private static ServiceLevelIndicatorMiddleware CreateMiddleware(
        RequestDelegate next,
        ServiceLevelIndicator sli,
        ILogger<ServiceLevelIndicatorMiddleware> logger,
        ServiceLevelIndicatorHttpOptions? options = null,
        IEnumerable<IEnrichment<WebEnrichmentContext>>? enrichments = null) =>
        new(next, sli, enrichments ?? [], logger, Options.Create(options ?? new ServiceLevelIndicatorHttpOptions()));

    private static void AssertOutcome(MetricCapture capture, string outcome)
    {
        capture.For("operation.duration").Should().ContainSingle().Which.Tags["Outcome"].Should().Be(outcome);
        capture.For("operation.duration.threshold.count").Should().ContainSingle().Which.Tags["Outcome"].Should().Be(outcome);
    }

    private static string UniqueOperation() => $"HttpHardening-{Guid.NewGuid():N}";

    private sealed class TestEnrichment(Action<WebEnrichmentContext> enrich) : IEnrichment<WebEnrichmentContext>
    {
        public bool EnrichOnCancellation => true;

        public ValueTask EnrichAsync(WebEnrichmentContext context, CancellationToken cancellationToken)
        {
            enrich(context);
            return ValueTask.CompletedTask;
        }
    }
}
