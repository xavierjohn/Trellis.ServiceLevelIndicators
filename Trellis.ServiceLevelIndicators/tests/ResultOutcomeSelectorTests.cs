namespace Trellis.ServiceLevelIndicators.Tests;

using System.Diagnostics;
using Microsoft.Extensions.Options;

public sealed class ResultOutcomeSelectorTests
{
    [Theory]
    [InlineData(false, SliOutcome.Success, "Success", ActivityStatusCode.Ok)]
    [InlineData(true, SliOutcome.Success, "Success", ActivityStatusCode.Ok)]
    [InlineData(false, SliOutcome.Failure, "Failure", ActivityStatusCode.Error)]
    [InlineData(true, SliOutcome.Failure, "Failure", ActivityStatusCode.Error)]
    [InlineData(false, SliOutcome.ClientError, "ClientError", ActivityStatusCode.Unset)]
    [InlineData(true, SliOutcome.ClientError, "ClientError", ActivityStatusCode.Unset)]
    [InlineData(false, SliOutcome.Ignored, "Ignored", ActivityStatusCode.Unset)]
    [InlineData(true, SliOutcome.Ignored, "Ignored", ActivityStatusCode.Unset)]
    public async Task Selector_receives_original_result_once_and_sets_both_metrics(
        bool asynchronous, SliOutcome outcome, string wireValue, ActivityStatusCode activityStatus)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        using var activity = new Activity("SelectedResult").Start();
        var original = new OperationResult(false);
        var completed = false;
        var classifierCalls = 0;
        var attributes = new KeyValuePair<string, object?>[] { new("Tier", "standard") };

        SliOutcome Classify(OperationResult result)
        {
            completed.Should().BeTrue();
            result.Should().BeSameAs(original);
            classifierCalls++;
            return outcome;
        }

        var returned = asynchronous
            ? await sli.MeasureAsync("Work", async () =>
            {
                await Task.Yield();
                completed = true;
                return original;
            }, Classify, attributes)
            : sli.Measure("Work", () =>
            {
                completed = true;
                return original;
            }, Classify, attributes);

        returned.Should().BeSameAs(original);
        classifierCalls.Should().Be(1);
        AssertOutcome(capture, wireValue);
        capture.Measurements.Should().OnlyContain(measurement => Equals(measurement.Tags["Tier"], "standard"));
        activity.Status.Should().Be(activityStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_result_is_passed_to_selector_and_returned(bool asynchronous)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var calls = 0;

        SliOutcome Classify(string? result)
        {
            result.Should().BeNull();
            calls++;
            return SliOutcome.ClientError;
        }

        var returned = asynchronous
            ? await sli.MeasureAsync("Work", () => Task.FromResult<string?>(null), Classify)
            : sli.Measure("Work", () => (string?)null, Classify);

        returned.Should().BeNull();
        calls.Should().Be(1);
        AssertOutcome(capture, "ClientError");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Value_type_result_can_be_classified_without_an_exception(bool asynchronous)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        static SliOutcome Classify(int result) => result < 0 ? SliOutcome.Failure : SliOutcome.Success;

        var returned = asynchronous
            ? await sli.MeasureAsync("Work", () => Task.FromResult(-1), Classify)
            : sli.Measure("Work", () => -1, Classify);

        returned.Should().Be(-1);
        AssertOutcome(capture, "Failure");
    }

    [Theory]
    [InlineData(false, "failure", "Failure")]
    [InlineData(true, "failure", "Failure")]
    [InlineData(false, "aggregate", "Failure")]
    [InlineData(true, "aggregate", "Failure")]
    [InlineData(false, "cancellation", "Ignored")]
    [InlineData(true, "cancellation", "Ignored")]
    public async Task Action_errors_propagate_unchanged_without_running_selector(
        bool asynchronous, string exceptionKind, string expectedOutcome)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var original = CreateException(exceptionKind);
        var classifierCalls = 0;

        SliOutcome Classify(OperationResult result)
        {
            classifierCalls++;
            return SliOutcome.Success;
        }

        var propagated = await Xunit.Record.ExceptionAsync(async () =>
        {
            if (asynchronous)
            {
                await sli.MeasureAsync<OperationResult>("Work", async () =>
                {
                    await Task.Yield();
                    throw original;
                }, Classify);
            }
            else
            {
                sli.Measure<OperationResult>("Work", () => throw original, Classify);
            }
        });

        propagated.Should().BeSameAs(original);
        classifierCalls.Should().Be(0);
        AssertOutcome(capture, expectedOutcome);
    }

    [Theory]
    [InlineData(false, "failure", "Failure")]
    [InlineData(true, "failure", "Failure")]
    [InlineData(false, "cancellation", "Ignored")]
    [InlineData(true, "cancellation", "Ignored")]
    public async Task Selector_errors_propagate_and_never_record_success(
        bool asynchronous, string exceptionKind, string expectedOutcome)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var original = CreateException(exceptionKind);
        var result = new OperationResult(true);
        var classifierCalls = 0;

        SliOutcome Classify(OperationResult value)
        {
            value.Should().BeSameAs(result);
            classifierCalls++;
            throw original;
        }

        var propagated = await Xunit.Record.ExceptionAsync(async () =>
        {
            if (asynchronous)
                await sli.MeasureAsync("Work", () => Task.FromResult(result), Classify);
            else
                sli.Measure("Work", () => result, Classify);
        });

        propagated.Should().BeSameAs(original);
        classifierCalls.Should().Be(1);
        AssertOutcome(capture, expectedOutcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undefined_selector_outcome_is_rejected_and_records_failure(bool asynchronous)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        static SliOutcome Classify(int _) => (SliOutcome)99;

        var error = await Xunit.Record.ExceptionAsync(async () =>
        {
            if (asynchronous)
                await sli.MeasureAsync("Work", () => Task.FromResult(42), Classify);
            else
                sli.Measure("Work", () => 42, Classify);
        });

        error.Should().BeOfType<ArgumentOutOfRangeException>()
            .Which.ActualValue.Should().Be((SliOutcome)99);
        AssertOutcome(capture, "Failure");
    }

    [Theory]
    [InlineData(false, "action")]
    [InlineData(true, "action")]
    [InlineData(false, "classifyOutcome")]
    [InlineData(true, "classifyOutcome")]
    public async Task Null_delegates_are_rejected_before_recording(bool asynchronous, string parameter)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var actionCalls = 0;
        Func<int> action = () => { actionCalls++; return 42; };
        Func<Task<int>> asyncAction = () => { actionCalls++; return Task.FromResult(42); };
        Func<int, SliOutcome> classifier = _ => SliOutcome.Success;
        if (parameter == "action")
        {
            action = null!;
            asyncAction = null!;
        }
        else
        {
            classifier = null!;
        }

        var error = await Xunit.Record.ExceptionAsync(async () =>
        {
            if (asynchronous)
                await sli.MeasureAsync("Work", asyncAction, classifier);
            else
                sli.Measure("Work", action, classifier);
        });

        error.Should().BeOfType<ArgumentNullException>().Which.ParamName.Should().Be(parameter);
        actionCalls.Should().Be(0);
        capture.Measurements.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_result_overloads_still_infer_success(bool asynchronous)
    {
        using var capture = new MetricCapture();
        using var sli = CreateSli(capture);
        var original = new OperationResult(false);
        var attributes = new KeyValuePair<string, object?>[] { new("Tier", "standard") };

        var returned = asynchronous
            ? await sli.MeasureAsync("Work", () => Task.FromResult(original), attributes)
            : sli.Measure("Work", () => original, attributes);

        returned.Should().BeSameAs(original);
        AssertOutcome(capture, "Success");
        capture.Measurements.Should().OnlyContain(measurement => Equals(measurement.Tags["Tier"], "standard"));
    }

    private static ServiceLevelIndicator CreateSli(MetricCapture capture)
    {
        var options = new ServiceLevelIndicatorOptions
        {
            Meter = capture.Meter,
            CustomerResourceId = "tenant-a",
            LocationId = "test-region"
        };
        options.LatencyThresholds.Add(TimeSpan.FromDays(1));
        return new ServiceLevelIndicator(Options.Create(options));
    }

    private static void AssertOutcome(MetricCapture capture, string outcome)
    {
        var duration = capture.For("operation.duration").Should().ContainSingle().Subject;
        duration.Tags["Outcome"].Should().Be(outcome);
        var count = capture.For("operation.duration.threshold.count").Should().ContainSingle().Subject;
        count.Tags["Outcome"].Should().Be(outcome);
        count.Tags["sli.latency.result"].Should().Be("within");
        count.Value.Should().Be(1);
        capture.Measurements.Should().HaveCount(2);
    }

    private static Exception CreateException(string kind) => kind switch
    {
        "failure" => new InvalidOperationException("Original failure"),
        "aggregate" => new AggregateException(new InvalidOperationException("Original failure")),
        "cancellation" => new OperationCanceledException("Original cancellation"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed record OperationResult(bool IsSuccess);
}
