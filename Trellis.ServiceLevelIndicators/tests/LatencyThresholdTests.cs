namespace Trellis.ServiceLevelIndicators.Tests;

using Microsoft.Extensions.Options;

public sealed class LatencyThresholdTests
{
    private const string CounterName = "operation.duration.threshold.count";

    [Fact]
    public void Threshold_counter_is_not_created_by_default()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);

        sli.Record("Work", 100, SliOutcome.Success);

        capture.Instruments.Should().NotContain(instrument => instrument.Name == CounterName);
        capture.For("operation.duration").Should().ContainSingle();
    }

    [Theory]
    [InlineData(-10000L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(9999L)]
    [InlineData(10001L)]
    public void Invalid_thresholds_fail_before_creating_instruments(long ticks)
    {
        using var capture = new MetricCapture();
        var options = CreateOptions(capture);
        options.LatencyThresholds.Add(TimeSpan.FromTicks(ticks));

        Action create = () =>
        {
            using var sli = new ServiceLevelIndicator(Options.Create(options));
        };

        create.Should().Throw<ArgumentException>()
            .Where(exception => exception.ParamName == "latencyThresholds");
        capture.Instruments.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_thresholds_are_rejected()
    {
        using var capture = new MetricCapture();
        var options = CreateOptions(capture);
        options.LatencyThresholds.Add(TimeSpan.FromSeconds(2));
        options.LatencyThresholds.Add(TimeSpan.FromMilliseconds(2000));

        Action create = () =>
        {
            using var sli = new ServiceLevelIndicator(Options.Create(options));
        };

        create.Should().Throw<ArgumentException>()
            .Where(exception => exception.ParamName == "latencyThresholds");
        capture.Instruments.Should().BeEmpty();
    }

    [Fact]
    public void Thresholds_are_snapshotted_at_construction()
    {
        using var capture = new MetricCapture();
        var options = CreateOptions(capture);
        options.LatencyThresholds.Add(TimeSpan.FromSeconds(2));
        options.LatencyThresholds.Add(TimeSpan.FromSeconds(10));
        using var sli = new ServiceLevelIndicator(Options.Create(options));
        options.LatencyThresholds.Clear();
        options.LatencyThresholds.Add(TimeSpan.FromMinutes(1));

        sli.Record("Work", 2500, SliOutcome.Success);

        var counts = capture.For(CounterName);
        counts.Should().HaveCount(2);
        counts.Should().ContainSingle(count =>
            Equals(count.Tags["sli.latency.threshold_ms"], 2000L) &&
            Equals(count.Tags["sli.latency.result"], "exceeded"));
        counts.Should().ContainSingle(count =>
            Equals(count.Tags["sli.latency.threshold_ms"], 10000L) &&
            Equals(count.Tags["sli.latency.result"], "within"));
    }

    [Theory]
    [InlineData(0L, "within")]
    [InlineData(1999L, "within")]
    [InlineData(2000L, "within")]
    [InlineData(2001L, "exceeded")]
    [InlineData(long.MaxValue, "exceeded")]
    public void Each_observation_increments_exactly_one_side(long elapsedMilliseconds, string result)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromSeconds(2));

        sli.Record("Work", elapsedMilliseconds, SliOutcome.Success);

        var count = capture.For(CounterName).Should().ContainSingle().Subject;
        count.Value.Should().Be(1);
        count.Instrument.Unit.Should().Be("{operation}");
        count.Instrument.Should().BeOfType<System.Diagnostics.Metrics.Counter<long>>();
        count.Instrument.Meter.Should().BeSameAs(capture.Meter);
        count.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["CustomerResourceId"] = "tenant-a",
            ["LocationId"] = "test-region",
            ["Operation"] = "Work",
            ["Outcome"] = "Success",
            ["sli.latency.threshold_ms"] = 2000L,
            ["sli.latency.result"] = result
        });
        capture.For("operation.duration").Should().ContainSingle()
            .Which.Value.Should().Be(elapsedMilliseconds);
    }

    [Theory]
    [InlineData(SliOutcome.Success, "Success")]
    [InlineData(SliOutcome.Failure, "Failure")]
    [InlineData(SliOutcome.ClientError, "ClientError")]
    [InlineData(SliOutcome.Ignored, "Ignored")]
    public void Timing_classification_does_not_change_outcome(SliOutcome outcome, string wireValue)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromSeconds(2));

        sli.Record("Work", 10, outcome);

        var count = capture.For(CounterName).Should().ContainSingle().Subject;
        count.Tags["Outcome"].Should().Be(wireValue);
        count.Tags["sli.latency.result"].Should().Be("within");
    }

    [Fact]
    public void All_record_overloads_feed_the_counter()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromSeconds(2));

        sli.Record("Default", 1);
        sli.Record("Outcome", 1, SliOutcome.Success);
        sli.Record("Resource", "tenant-b", 1);
        sli.Record("ResourceOutcome", "tenant-b", 1, SliOutcome.Failure);

        var counts = capture.For(CounterName);
        counts.Should().HaveCount(4);
        counts.Sum(count => count.Value).Should().Be(4);
        counts.Single(count => Equals(count.Tags["Operation"], "ResourceOutcome"))
            .Tags["CustomerResourceId"].Should().Be("tenant-b");
        counts.Single(count => Equals(count.Tags["Operation"], "Default"))
            .Tags["Outcome"].Should().Be("Ignored");
    }

    [Fact]
    public void Scope_uses_final_dimensions_and_records_only_once()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromSeconds(2));
        var operation = sli.StartMeasuring("Original");
        operation.Operation = "Final";
        operation.CustomerResourceId = "tenant-b";
        operation.SetOutcome(SliOutcome.Failure);
        operation.AddAttribute("http.api.version", "2026-07-04");
        operation.AddAttribute("ProductTier", "standard");

        operation.Dispose();
        operation.Dispose();

        var count = capture.For(CounterName).Should().ContainSingle().Subject;
        count.Tags["Operation"].Should().Be("Final");
        count.Tags["CustomerResourceId"].Should().Be("tenant-b");
        count.Tags["Outcome"].Should().Be("Failure");
        count.Tags["http.api.version"].Should().Be("2026-07-04");
        count.Tags["ProductTier"].Should().Be("standard");
        capture.For("operation.duration").Should().ContainSingle();
    }

    [Fact]
    public void Counter_projection_preserves_grouping_and_does_not_modify_histogram_tags()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        using (var operation = sli.StartMeasuring("GET /items/{id}"))
        {
            operation.Attributes.Add(new("http.request.method", "GET"));
            operation.Attributes.Add(new("http.response.status.code", 200));
            operation.Attributes.Add(new("sli.request.aborted", true));
            operation.Attributes.Add(new("sli.request.abort_exception", "other"));
            operation.AddAttribute("http.api.version", "2026-07-04");
            operation.AddAttribute("ProductTier", "standard");
            operation.AddAttribute("Environment", "test");
            operation.AddAttribute("Provider", "maps");
            operation.AddAttribute("Region", "west");
        }

        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags.Should().ContainKeys("http.request.method", "http.response.status.code", "sli.request.abort_exception");
        duration.Tags.Should().NotContainKeys("sli.latency.threshold_ms", "sli.latency.result");
        var expected = duration.Tags
            .Where(tag => tag.Key is not ("http.request.method" or "http.response.status.code" or "sli.request.abort_exception"))
            .ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);
        capture.For(CounterName).Should().HaveCount(2).And.AllSatisfy(count =>
        {
            count.Tags.Where(tag => tag.Key is not ("sli.latency.threshold_ms" or "sli.latency.result"))
                .Should().BeEquivalentTo(expected);
            count.Tags.Should().HaveCount(expected.Count + 2);
        });
    }

    [Fact]
    public async Task Existing_helpers_feed_success_failure_and_cancellation_counts()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromDays(1));

        sli.Measure("Action", () => { });
        sli.Measure("Function", () => 42).Should().Be(42);
        await sli.MeasureAsync("AsyncAction", () => Task.CompletedTask);
        (await sli.MeasureAsync("AsyncFunction", () => Task.FromResult(42))).Should().Be(42);
        Action failure = () => sli.Measure("Failure", () => throw new InvalidOperationException("failed"));
        failure.Should().Throw<InvalidOperationException>();
        Func<Task> asyncFailure = () => sli.MeasureAsync("AsyncFailure", () => Task.FromException(new InvalidOperationException("failed")));
        await asyncFailure.Should().ThrowAsync<InvalidOperationException>();
        Action cancellation = () => sli.Measure("Cancellation", () => throw new OperationCanceledException());
        cancellation.Should().Throw<OperationCanceledException>();
        Func<Task> asyncCancellation = () => sli.MeasureAsync("AsyncCancellation", () => Task.FromException(new OperationCanceledException()));
        await asyncCancellation.Should().ThrowAsync<OperationCanceledException>();

        var counts = capture.For(CounterName);
        counts.Should().HaveCount(8);
        counts.Count(count => Equals(count.Tags["Outcome"], "Success")).Should().Be(4);
        counts.Count(count => Equals(count.Tags["Outcome"], "Failure")).Should().Be(2);
        counts.Count(count => Equals(count.Tags["Outcome"], "Ignored")).Should().Be(2);
        capture.For("operation.duration").Should().HaveCount(8);
    }

    [Fact]
    public void No_traffic_produces_no_counter_measurements()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromSeconds(2));

        capture.Instruments.Should().ContainSingle(instrument => instrument.Name == CounterName);
        capture.Measurements.Should().BeEmpty();
    }

    [Theory]
    [InlineData("sli.latency.threshold_ms")]
    [InlineData("sli.latency.result")]
    public void Direct_attribute_mutation_cannot_collide_with_generated_threshold_dimensions(string name)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromSeconds(2));
        using var diagnostics = new InstrumentationDiagnosticsCapture("Work");
        var operation = sli.StartMeasuring("Work");
        operation.Attributes.Add(new(name, "override"));

        Action dispose = operation.Dispose;

        dispose.Should().NotThrow();
        diagnostics.Failures.Should().ContainSingle()
            .Which.Data["error"].Should().BeOfType<string>().Which.Should().Contain("added more than once");
        capture.Measurements.Should().BeEmpty();
    }

    [Fact]
    public void Pilot_ratio_counts_fast_failures_and_slow_aborts_as_misses()
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        sli.Record("Work", 1000, SliOutcome.Success);
        sli.Record("Work", 3000, SliOutcome.Success);
        sli.Record("Work", 10, SliOutcome.Failure);
        sli.Record("Work", 3000, SliOutcome.Failure);
        sli.RecordMeasurement("Work", "tenant-a", 3000, SliOutcome.Ignored, new KeyValuePair<string, object?>("sli.request.aborted", true));
        sli.RecordMeasurement("Work", "tenant-a", 1000, SliOutcome.Ignored, new KeyValuePair<string, object?>("sli.request.aborted", true));
        sli.Record("Work", 10, SliOutcome.ClientError);
        sli.Record("Work", 10, SliOutcome.Ignored);

        var counts = capture.For(CounterName);
        var policy = EvaluatePilotPolicy(counts, 2000);

        counts.Sum(count => count.Value).Should().Be(16);
        policy.Good.Should().Be(1);
        policy.Eligible.Should().Be(5);
        policy.Compliance.Should().Be(0.2);
        EvaluatePilotPolicy(counts, 10000).Compliance.Should().Be(0.5);
    }

    [Theory]
    [InlineData(SliOutcome.Failure, 1L, 0.0)]
    [InlineData(SliOutcome.ClientError, 0L, null)]
    [InlineData(SliOutcome.Ignored, 0L, null)]
    [InlineData(null, 0L, null)]
    public void Pilot_ratio_distinguishes_zero_good_events_from_no_data(
        SliOutcome? outcome,
        long expectedEligible,
        double? expectedCompliance)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture, TimeSpan.FromSeconds(2));
        if (outcome is { } recordedOutcome)
            sli.Record("Work", 10, recordedOutcome);

        var policy = EvaluatePilotPolicy(capture.For(CounterName), 2000);

        policy.Good.Should().Be(0);
        policy.Eligible.Should().Be(expectedEligible);
        policy.Compliance.Should().Be(expectedCompliance);
    }

    [Fact]
    public void Configured_meter_remains_owned_by_the_caller()
    {
        using var capture = new MetricCapture();
        var sli = CreateSli(capture, TimeSpan.FromSeconds(2));

        sli.Dispose();
        capture.Meter.CreateCounter<long>("caller.probe").Add(1);

        capture.For("caller.probe").Should().ContainSingle();
    }

    private static ServiceLevelIndicator CreateSli(MetricCapture capture, params TimeSpan[] thresholds)
    {
        var options = CreateOptions(capture);
        foreach (var threshold in thresholds)
            options.LatencyThresholds.Add(threshold);
        return new ServiceLevelIndicator(Options.Create(options));
    }

    private static ServiceLevelIndicatorOptions CreateOptions(MetricCapture capture) => new()
    {
        Meter = capture.Meter,
        CustomerResourceId = "tenant-a",
        LocationId = "test-region"
    };

    private static (long Good, long Eligible, double? Compliance) EvaluatePilotPolicy(
        IEnumerable<RecordedMeasurement> measurements,
        long thresholdMilliseconds)
    {
        var counts = measurements
            .Where(count => Equals(count.Tags["sli.latency.threshold_ms"], thresholdMilliseconds)).ToArray();
        var good = counts.Where(count =>
            Equals(count.Tags["Outcome"], "Success") &&
            Equals(count.Tags["sli.latency.result"], "within")).Sum(count => count.Value);
        var eligible = counts.Where(count =>
            count.Tags["Outcome"] is "Success" or "Failure" ||
            (Equals(count.Tags["Outcome"], "Ignored") &&
             count.Tags.TryGetValue("sli.request.aborted", out var aborted) && Equals(aborted, true) &&
             Equals(count.Tags["sli.latency.result"], "exceeded"))).Sum(count => count.Value);
        double? compliance = eligible > 0 ? (double)good / eligible : null;
        return (good, eligible, compliance);
    }
}
