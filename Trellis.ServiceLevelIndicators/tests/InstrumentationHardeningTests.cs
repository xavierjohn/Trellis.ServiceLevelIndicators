namespace Trellis.ServiceLevelIndicators.Tests;

using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class InstrumentationHardeningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reusing_default_meter_options_creates_independently_owned_meters(bool disposeFirstBeforeCreatingSecond)
    {
        var options = new ServiceLevelIndicatorOptions
        {
            CustomerResourceId = "tenant-a",
            LocationId = "test-region"
        };
        options.LatencyThresholds.Add(TimeSpan.FromDays(1));
        using var first = new ServiceLevelIndicator(Options.Create(options));
        if (disposeFirstBeforeCreatingSecond)
            first.Dispose();
        using var second = new ServiceLevelIndicator(Options.Create(options));
        var secondMeter = second.ServiceLevelIndicatorOptions.Meter;
        var durationCount = 0;
        var thresholdCount = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, secondMeter))
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
        {
            if (instrument.Name == "operation.duration")
                durationCount++;
            if (instrument.Name == "operation.duration.threshold.count")
                thresholdCount++;
        });
        listener.Start();

        first.Dispose();
        second.Record("Work", 1, SliOutcome.Success);

        options.Meter.Should().BeNull();
        first.ServiceLevelIndicatorOptions.Should().NotBeSameAs(options);
        second.ServiceLevelIndicatorOptions.Should().NotBeSameAs(first.ServiceLevelIndicatorOptions);
        secondMeter.Should().NotBeSameAs(first.ServiceLevelIndicatorOptions.Meter);
        durationCount.Should().Be(1);
        thresholdCount.Should().Be(1);
    }

    [Fact]
    public void Effective_options_copy_preserves_derived_options_and_isolates_configuration_changes()
    {
        using var capture = new MetricCapture();
        var configured = new DerivedOptions
        {
            CustomerResourceId = "tenant-a",
            LocationId = "original-region",
            Meter = capture.Meter,
            ConsumerSetting = "retained"
        };
        configured.LatencyThresholds.Add(TimeSpan.FromDays(1));
        using var sli = new ServiceLevelIndicator(Options.Create<ServiceLevelIndicatorOptions>(configured));

        configured.LocationId = "changed-region";
        configured.LatencyThresholds.Clear();
        sli.ServiceLevelIndicatorOptions.CustomerResourceId = "instance-tenant";
        sli.Record("Work", 1, SliOutcome.Success);

        sli.ServiceLevelIndicatorOptions.Should().BeOfType<DerivedOptions>()
            .Which.ConsumerSetting.Should().Be("retained");
        configured.CustomerResourceId.Should().Be("tenant-a");
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["LocationId"].Should().Be("original-region");
        duration.Tags["CustomerResourceId"].Should().Be("instance-tenant");
        configured.Meter.Should().BeSameAs(capture.Meter);
        sli.ServiceLevelIndicatorOptions.LatencyThresholds.Should().Equal(TimeSpan.FromDays(1));
        capture.For("operation.duration.threshold.count").Should().ContainSingle();
    }

    [Fact]
    public void Container_injects_recording_logger_without_requiring_logging_for_the_original_constructor()
    {
        using var capture = new MetricCapture();
        var logger = new DiagnosticTestLogger<ServiceLevelIndicator>();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<ServiceLevelIndicator>>(logger);
        services.AddServiceLevelIndicator(options =>
        {
            options.Meter = capture.Meter;
            options.LocationId = "test-region";
            options.CustomerResourceId = "tenant-a";
        });
        using var provider = services.BuildServiceProvider();
        var sli = provider.GetRequiredService<ServiceLevelIndicator>();
        var operation = sli.StartMeasuring(UniqueOperation());
        operation.Attributes.Add(new("Outcome", "invalid"));

        operation.Dispose();

        logger.Entries.Should().ContainSingle()
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void SetOutcome_rejects_undefined_values_before_changing_the_measurement()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        using (var operation = sli.StartMeasuring(UniqueOperation()))
        {
            operation.SetOutcome(SliOutcome.Success);
            Action set = () => operation.SetOutcome((SliOutcome)99);
            set.Should().Throw<ArgumentOutOfRangeException>();
        }

        capture.For("operation.duration").Should().ContainSingle()
            .Which.Tags["Outcome"].Should().Be("Success");
    }

    [Fact]
    public void Record_rejects_undefined_values_before_any_metric_or_activity_side_effects()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, customerResourceId: ServiceLevelIndicator.UnknownCustomerResourceId);
        using var activity = new System.Diagnostics.Activity("InvalidRecord").Start();
        activity.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);

        Action record = () => sli.Record("Work", 1, (SliOutcome)99);

        record.Should().Throw<ArgumentOutOfRangeException>();
        capture.Measurements.Should().BeEmpty();
        activity.Status.Should().Be(System.Diagnostics.ActivityStatusCode.Ok);
    }

    [Fact]
    public void Explicit_Record_still_surfaces_provider_failures_to_the_caller()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var original = new InvalidOperationException("Metric provider failed");
        using var listener = FaultingListener(capture.Meter, "operation.duration", () => throw original);

        Action record = () => sli.Record(UniqueOperation(), 1, SliOutcome.Success);

        record.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_recording_failure_does_not_replace_a_successfully_returned_result(bool asynchronous)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        using var listener = FaultingListener(capture.Meter, "operation.duration",
            () => throw new InvalidOperationException("Metric provider failed"));
        var original = new object();

        var returned = asynchronous
            ? await sli.MeasureAsync(name, async () =>
            {
                await Task.Yield();
                return original;
            }, _ => SliOutcome.Success)
            : sli.Measure(name, () => original, _ => SliOutcome.Success);

        returned.Should().BeSameAs(original);
        diagnostics.Failures.Should().ContainSingle().Which.Name.Should().Be("MeasurementRecordingFailed");
    }

    [Fact]
    public void The_logging_constructor_rejects_a_null_logger_before_creating_instruments()
    {
        using var capture = new MetricCapture();
        var options = Options.Create(new ServiceLevelIndicatorOptions
        {
            Meter = capture.Meter,
            LocationId = "test-region"
        });

        Action construct = () =>
        {
            using var sli = new ServiceLevelIndicator(options, null!);
        };

        construct.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("logger");
        capture.Instruments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("reserved")]
    [InlineData("operation")]
    public void Malformed_mutable_state_is_reported_once_and_never_retried(string kind)
    {
        using var capture = new MetricCapture();
        var logger = new DiagnosticTestLogger<ServiceLevelIndicator>();
        using var sli = CreateSli(capture, logger);
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(kind == "operation" ? " " : name);
        var operation = sli.StartMeasuring(name);
        switch (kind)
        {
            case "duplicate":
                operation.Attributes.Add(new("Tier", "first"));
                operation.Attributes.Add(new("Tier", "second"));
                break;
            case "blank":
                operation.Attributes.Add(new(" ", "invalid"));
                break;
            case "reserved":
                operation.Attributes.Add(new("Outcome", "invalid"));
                break;
            case "operation":
                operation.Operation = " ";
                break;
        }

        operation.Dispose();
        operation.Attributes.Clear();
        operation.Operation = name;
        operation.SetOutcome(SliOutcome.Success);
        operation.Dispose();

        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        capture.Measurements.Should().BeEmpty();
        diagnostics.Failures.Should().ContainSingle().Which.Name.Should().Be("MeasurementRecordingFailed");
    }

    [Theory]
    [InlineData("operation.duration", "tenant-a")]
    [InlineData("operation.duration.threshold.count", "tenant-a")]
    [InlineData("sli.diagnostics.unknown_customer_resource_id", ServiceLevelIndicator.UnknownCustomerResourceId)]
    public void Recording_provider_failure_is_reported_and_disposal_is_terminal(string instrumentName, string customerResourceId)
    {
        using var capture = new MetricCapture();
        var logger = new DiagnosticTestLogger<ServiceLevelIndicator>();
        using var sli = CreateSli(capture, logger, customerResourceId);
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        var failure = new InvalidOperationException("Metric provider failed");
        var calls = 0;
        using var listener = FaultingListener(capture.Meter, instrumentName, () =>
        {
            calls++;
            throw failure;
        });
        var operation = sli.StartMeasuring(name);

        operation.Dispose();
        operation.Dispose();

        calls.Should().Be(1);
        logger.Entries.Should().ContainSingle().Which.Exception.Should().BeSameAs(failure);
        diagnostics.Failures.Should().ContainSingle().Which.Name.Should().Be("MeasurementRecordingFailed");
        capture.For("operation.duration").Length.Should().BeLessThanOrEqualTo(1);
        capture.For("operation.duration.threshold.count").Length.Should().BeLessThanOrEqualTo(1);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Recording_failure_never_masks_action_or_selector_errors(
        bool asynchronous, bool selectorFails, bool cancellation)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        Exception original = cancellation
            ? new OperationCanceledException("Original cancellation")
            : new InvalidOperationException("Original action or selector failure");
        using var listener = FaultingListener(capture.Meter, "operation.duration",
            () => throw new InvalidOperationException("Metric provider failed"));
        int Action() => selectorFails ? 42 : throw original;
        SliOutcome Classify(int _) => throw original;

        var propagated = await Xunit.Record.ExceptionAsync(async () =>
        {
            if (asynchronous)
                await sli.MeasureAsync(name, async () =>
                {
                    await Task.Yield();
                    return Action();
                }, Classify);
            else
                sli.Measure(name, Action, Classify);
        });

        propagated.Should().BeSameAs(original);
        diagnostics.Failures.Should().ContainSingle().Which.Name.Should().Be("MeasurementRecordingFailed");
    }

    [Theory]
    [InlineData("IsEnabled")]
    [InlineData("Log")]
    public void A_broken_core_logger_cannot_mask_the_application_exception(string throwFrom)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, new DiagnosticTestLogger<ServiceLevelIndicator>(throwFrom));
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        var original = new InvalidOperationException("Original application failure");

        Action invoke = () =>
        {
            using var operation = sli.StartMeasuring(name);
            operation.Attributes.Add(new("Outcome", "invalid"));
            throw original;
        };

        var propagated = Xunit.Record.Exception(invoke);

        propagated.Should().BeSameAs(original);
        diagnostics.Failures.Select(failure => failure.Name).Should()
            .BeEquivalentTo("MeasurementRecordingFailed", "DiagnosticLoggingFailed");
    }

    [Fact]
    public void Even_an_exception_with_a_throwing_ToString_cannot_escape_diagnostic_reporting()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        using var listener = FaultingListener(capture.Meter, "operation.duration", () => throw new UnformattableException());

        sli.StartMeasuring(name).Dispose();

        diagnostics.Failures.Should().ContainSingle()
            .Which.Data["error"].Should().Be(typeof(UnformattableException).FullName);
    }

    [Fact]
    public void A_throwing_diagnostic_observer_cannot_replace_the_application_exception()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var name = UniqueOperation();
        using var listener = new ThrowingDiagnosticListener(name);
        var original = new InvalidOperationException("Original application failure");
        Action invoke = () =>
        {
            using var operation = sli.StartMeasuring(name);
            operation.Attributes.Add(new("Outcome", "invalid"));
            throw original;
        };

        var propagated = Xunit.Record.Exception(invoke);

        propagated.Should().BeSameAs(original);
        listener.Attempts.Should().Be(1);
    }

    [Fact]
    public void Reentrant_disposal_records_only_once()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var operation = sli.StartMeasuring(UniqueOperation());
        var calls = 0;
        using var listener = FaultingListener(capture.Meter, "operation.duration", () =>
        {
            calls++;
            operation.Dispose();
        });

        operation.Dispose();

        calls.Should().Be(1);
        capture.For("operation.duration").Should().ContainSingle();
        capture.For("operation.duration.threshold.count").Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_failed_disposal_attempts_only_one_recording()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var name = UniqueOperation();
        using var diagnostics = new InstrumentationDiagnosticsCapture(name);
        var calls = 0;
        using var listener = FaultingListener(capture.Meter, "operation.duration", () =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Metric provider failed");
        });
        var operation = sli.StartMeasuring(name);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(operation.Dispose, TestContext.Current.CancellationToken)));

        calls.Should().Be(1);
        diagnostics.Failures.Should().ContainSingle();
    }

    private static ServiceLevelIndicator CreateSli(
        MetricCapture capture, ILogger<ServiceLevelIndicator>? logger = null, string customerResourceId = "tenant-a")
    {
        var options = new ServiceLevelIndicatorOptions
        {
            Meter = capture.Meter,
            LocationId = "test-region",
            CustomerResourceId = customerResourceId
        };
        options.LatencyThresholds.Add(TimeSpan.FromDays(1));
        return logger is null
            ? new ServiceLevelIndicator(Options.Create(options))
            : new ServiceLevelIndicator(Options.Create(options), logger);
    }

    private static MeterListener FaultingListener(Meter meter, string instrumentName, Action callback)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == instrumentName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => callback());
        listener.Start();
        return listener;
    }

    private static string UniqueOperation() => $"Hardening-{Guid.NewGuid():N}";

    private sealed class DerivedOptions : ServiceLevelIndicatorOptions
    {
        public string ConsumerSetting { get; init; } = string.Empty;
    }

    private sealed class UnformattableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("Cannot format exception");
    }

    private sealed class ThrowingDiagnosticListener(string operation) : EventListener
    {
        public int Attempts { get; private set; }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Trellis-ServiceLevelIndicators")
                EnableEvents(eventSource, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.Payload is not null && eventData.Payload.Count > 0 && Equals(eventData.Payload[0], operation))
            {
                Attempts++;
                throw new InvalidOperationException("Diagnostic observer failed");
            }
        }
    }
}
