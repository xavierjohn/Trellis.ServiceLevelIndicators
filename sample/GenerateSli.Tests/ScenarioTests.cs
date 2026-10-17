namespace GenerateSli.Tests;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

internal static class TestTimeout
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(5);

    public static Task WithTestTimeout(this Task task) =>
        task.WaitAsync(Default, TestContext.Current.CancellationToken);

    public static Task<T> WithTestTimeout<T>(this Task<T> task) =>
        task.WaitAsync(Default, TestContext.Current.CancellationToken);
}

public sealed class ScenarioTests
{
    [Fact]
    public void Continuous_mode_is_explicit_and_preserves_finite_defaults()
    {
        var defaults = GeneratorOptions.Parse([]);
        defaults.Continuous.Should().BeFalse();
        defaults.WaitForReady.Should().BeFalse();
        defaults.PhaseSeconds.Should().Be(30);
        defaults.RequestsPerSecond.Should().Be(12);

        var continuous = GeneratorOptions.Parse(
            ["--continuous", "--wait-for-ready", "--base-url", "http://demo-api:8080"]);
        continuous.Continuous.Should().BeTrue();
        continuous.WaitForReady.Should().BeTrue();
        continuous.BaseAddress.Should().Be(new Uri("http://demo-api:8080/"));
    }

    [Theory]
    [InlineData("--base-url", "ftp://localhost")]
    [InlineData("--base-url", "/relative")]
    [InlineData("--phase-seconds", "0")]
    [InlineData("--requests-per-second", "-1")]
    [InlineData("--unknown", "value")]
    public void Invalid_options_fail_explicitly(string option, string value)
    {
        var act = () => GeneratorOptions.Parse([option, value]);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("\t1")]
    [InlineData("1.0")]
    [InlineData("1,000")]
    public void Numeric_options_require_invariant_digits(string value)
    {
        foreach (var option in new[] { "--phase-seconds", "--requests-per-second" })
        {
            var act = () => GeneratorOptions.Parse([option, value]);
            act.Should().Throw<ArgumentException>().WithMessage("*only digits*");
        }
    }

    [Theory]
    [InlineData("--phase-seconds", "3601")]
    [InlineData("--phase-seconds", "2147483647")]
    [InlineData("--requests-per-second", "1001")]
    [InlineData("--requests-per-second", "2000000000")]
    public void Numeric_options_reject_values_beyond_the_demo_limits(string option, string value)
    {
        var act = () => GeneratorOptions.Parse([option, value]);
        act.Should().Throw<ArgumentException>().WithMessage("*between 1 and*");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 12)]
    [InlineData(3600, 1000)]
    public void Accepted_numeric_limits_keep_the_pacing_interval_positive(int phaseSeconds, int requestsPerSecond)
    {
        var options = GeneratorOptions.Parse(
            ["--phase-seconds", phaseSeconds.ToString(CultureInfo.InvariantCulture),
             "--requests-per-second", requestsPerSecond.ToString(CultureInfo.InvariantCulture)]);

        options.PhaseSeconds.Should().Be(phaseSeconds);
        options.RequestsPerSecond.Should().Be(requestsPerSecond);
        TimeSpan.FromSeconds(1d / options.RequestsPerSecond).Ticks.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("--base-url")]
    [InlineData("--phase-seconds")]
    [InlineData("--requests-per-second")]
    public void Value_options_require_a_value_instead_of_consuming_the_next_flag(string option)
    {
        var missing = () => GeneratorOptions.Parse([option]);
        var nextFlag = () => GeneratorOptions.Parse([option, "--continuous"]);

        missing.Should().Throw<ArgumentException>().WithMessage($"Missing value for '{option}'.");
        nextFlag.Should().Throw<ArgumentException>().WithMessage($"Missing value for '{option}'.");
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("Incident")]
    [InlineData("ClientError")]
    [InlineData("Throttled")]
    [InlineData("Recovery")]
    public void Every_phase_keeps_healthy_reads_and_writes_for_each_project(string phaseName)
    {
        var phase = Enum.Parse<ScenarioPhase>(phaseName);
        var reads = new HashSet<string>(StringComparer.Ordinal);
        var healthyWrites = new HashSet<string>(StringComparer.Ordinal);
        for (var number = 1; number <= 600; number++)
        {
            using var request = ScenarioTraffic.CreateRequest(phase, number);
            var path = request.RequestUri!.OriginalString;
            var project = path.Split('/')[2];
            if (request.Method == HttpMethod.Get)
            {
                path.Should().NotContain("?");
                reads.Add(project);
            }
            else if (path.EndsWith("mode=healthy", StringComparison.Ordinal))
            {
                healthyWrites.Add(project);
            }
        }

        string[] projects = ["project-blue", "project-green", "project-red"];
        reads.Should().BeEquivalentTo(projects);
        healthyWrites.Should().BeEquivalentTo(projects);
    }

    [Theory]
    [InlineData("Incident", "project-red", "failure,healthy,slow")]
    [InlineData("Incident", "project-green", "healthy")]
    [InlineData("ClientError", "project-blue", "client-error,healthy")]
    [InlineData("ClientError", "project-red", "healthy")]
    [InlineData("Throttled", "project-red", "healthy,throttled")]
    [InlineData("Throttled", "project-blue", "healthy")]
    public void Incidents_affect_only_the_intended_project(string phaseName, string project, string expectedModes)
    {
        var phase = Enum.Parse<ScenarioPhase>(phaseName);
        var modes = new HashSet<string>(StringComparer.Ordinal);
        for (var number = 1; number <= 600; number++)
        {
            using var request = ScenarioTraffic.CreateRequest(phase, number);
            var path = request.RequestUri!.OriginalString;
            if (request.Method == HttpMethod.Post && path.StartsWith($"api/projects/{project}/", StringComparison.Ordinal))
                modes.Add(path[(path.IndexOf("mode=", StringComparison.Ordinal) + 5)..]);
        }

        modes.Should().BeEquivalentTo(expectedModes.Split(','));
    }

    [Fact]
    public async Task Virtual_time_waits_for_timer_registration_instead_of_spinning()
    {
        var time = new TimerObservingTimeProvider();
        var started = time.GetTimestamp();
        var registration = time.WaitForTimerAsync();

        await TimerObservingTimeProvider.SettleAsync();
        registration.IsCompleted.Should().BeFalse();
        time.GetElapsedTime(started).Should().Be(TimeSpan.Zero);

        var delay = Task.Delay(TimeSpan.FromMinutes(2), time, TestContext.Current.CancellationToken);
        (await registration).Should().Be(TimeSpan.FromMinutes(2));
        time.GetElapsedTime(started).Should().Be(TimeSpan.Zero, "observing a timer must not advance time");
        delay.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromMinutes(2));
        await delay.WithTestTimeout();
    }

    [Fact]
    public async Task Advancing_virtual_time_honors_the_timeout_when_callbacks_keep_registering_timers()
    {
        var time = new TimerObservingTimeProvider();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var failSafe = TimeSpan.FromSeconds(2);
        var failSafeTripped = false;
        ITimer? last = null;
        TimerCallback? register = null;
        register = _ =>
        {
            // The chain runs synchronously inside Advance, so a regressed deadline check would never return
            // control to the test. This independent fail-safe ends the chain and lets the assertions fail.
            if (stopwatch.Elapsed > failSafe)
            {
                failSafeTripped = true;
                return;
            }

            last = time.CreateTimer(register!, null, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        };

        try
        {
            last = time.CreateTimer(register, null, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);

            var act = () => time.AdvanceUntilAsync(new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(200));

            await act.Should().ThrowAsync<TimeoutException>();
            failSafeTripped.Should().BeFalse("AdvanceUntilAsync must stop on its own deadline, not rely on the fail-safe");
            stopwatch.Elapsed.Should().BeLessThan(failSafe);
        }
        finally
        {
            failSafeTripped = true;
            last?.Dispose();
        }
    }

    [Fact]
    public async Task Virtual_time_skips_timers_that_were_disposed_before_they_were_observed()
    {
        var time = new TimerObservingTimeProvider();
        var started = time.GetTimestamp();
        using (new CancellationTokenSource(TimeSpan.FromHours(1), time))
        {
        }

        var delay = Task.Delay(TimeSpan.FromSeconds(10), time, TestContext.Current.CancellationToken);
        await time.AdvanceUntilAsync(delay);

        time.GetElapsedTime(started).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Finite_mode_completes_exactly_one_cycle()
    {
        using var client = CreateClient(_ => new(HttpStatusCode.OK));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new TimerObservingTimeProvider();

        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2), output, error, TestContext.Current.CancellationToken, time);
        await time.AdvanceUntilAsync(run);
        await run.WithTestTimeout();

        output.ToString().Should().Contain("Cycle 1").And.NotContain("Cycle 2").And.Contain("Scenario complete.");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Continuous_mode_starts_another_cycle_and_honors_cancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var client = CreateClient(_ => new(HttpStatusCode.OK));
        using var output = new CycleCancellingWriter(cancellation, "Cycle 2");
        using var error = new StringWriter();
        var time = new TimerObservingTimeProvider();

        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2, Continuous: true), output, error, cancellation.Token, time);
        await time.AdvanceUntilAsync(run);

        var act = () => run.WithTestTimeout();
        await act.Should().ThrowAsync<OperationCanceledException>();
        output.ToString().Should().Contain("Cycle 1").And.Contain("Cycle 2").And.NotContain("Scenario complete.");
        error.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "transport-failures", "failed")]
    [InlineData(true, "timeouts", "timed out")]
    public async Task Continuous_mode_reports_transient_failures_immediately_and_recovers(
        bool timeout, string countName, string diagnostic)
    {
        var requests = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/health" && Interlocked.Increment(ref requests) == 1)
            {
                if (timeout)
                    throw new TaskCanceledException("Simulated request timeout.");

                throw new HttpRequestException("Simulated API restart.");
            }

            return new(HttpStatusCode.OK);
        });
        using var output = new CycleCancellingWriter(cancellation, "Cycle 2");
        using var error = new StringWriter();
        var time = new TimerObservingTimeProvider();

        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2, Continuous: true), output, error, cancellation.Token, time);

        run.IsCompleted.Should().BeFalse();
        error.ToString().Should().Contain(diagnostic).And.Contain("Continuing traffic.");

        await time.AdvanceUntilAsync(run);
        var act = () => run.WithTestTimeout();
        await act.Should().ThrowAsync<OperationCanceledException>();

        requests.Should().BeGreaterThan(1);
        output.ToString().Should().Contain($"{countName}=1").And.Contain("200=").And.Contain("Cycle 2");
        output.ToString().Should().NotContain(timeout ? "transport-failures=" : "timeouts=");
    }

    [Theory]
    [InlineData(false, "transport-failures", "failed")]
    [InlineData(true, "timeouts", "timed out")]
    public async Task Repeated_continuous_failures_are_counted_without_per_request_log_flooding(
        bool timeout, string countName, string diagnostic)
    {
        var requests = 0;
        var time = new FakeTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
                return new(HttpStatusCode.OK);

            Interlocked.Increment(ref requests);
            time.Advance(TimeSpan.FromMilliseconds(100));
            if (timeout)
                throw new TaskCanceledException("Simulated request timeout.");

            throw new HttpRequestException("Simulated API outage.");
        });
        using var output = new CycleCancellingWriter(cancellation, "Cycle 2");
        using var error = new StringWriter();

        var act = () => ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 1_000, Continuous: true), output, error, cancellation.Token, time);

        await act.Should().ThrowAsync<OperationCanceledException>();

        var diagnostics = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        diagnostics.Should().HaveCount(5, "each phase logs only its first failure of each kind");
        diagnostics.Should().OnlyContain(line => line.Contains(diagnostic, StringComparison.Ordinal));
        var summaries = Regex.Matches(output.ToString(), countName + @"=(\d+)");
        summaries.Should().HaveCount(5);
        summaries.Sum(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Should().Be(requests);
        requests.Should().Be(50, "each of five phases contains ten failures with 100 ms of simulated send overhead");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finite_mode_stops_sending_immediately_after_a_transport_failure_or_timeout(bool timeout)
    {
        var requests = 0;
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
                return new(HttpStatusCode.OK);

            Interlocked.Increment(ref requests);
            if (timeout)
                throw new TaskCanceledException("Simulated request timeout.");

            throw new HttpRequestException("Simulated API restart.");
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new FakeTimeProvider();

        var act = () => ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 30, 1), output, error, TestContext.Current.CancellationToken, time)
            .WithTestTimeout();

        await act.Should().ThrowAsync<Exception>()
            .Where(ex => timeout ? ex is OperationCanceledException : ex is HttpRequestException);
        requests.Should().Be(1, "a failed finite run must not keep sending for the rest of its phase");
        output.ToString().Should().NotContain("Scenario complete.");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task User_cancellation_takes_precedence_over_an_earlier_failure_while_draining()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var client = new PayloadObservingClient(new ResponseHandler((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            if (Interlocked.Increment(ref requests) == 1)
                return first.Task;

            secondToken.TrySetResult(token);
            return second.Task;
        }))
        { BaseAddress = new Uri("http://demo.test/") };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new TimerObservingTimeProvider();
        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 30, 10), output, error, cancellation.Token, time);

        try
        {
            await time.AdvanceUntilAsync(secondToken.Task);
            var requestToken = await secondToken.Task;
            first.SetException(new HttpRequestException("An earlier request failed."));

            // The runner cancels the other in-flight requests only after it has recorded the failure,
            // so shutdown is requested while that failure is already waiting to be rethrown.
            var recorded = () => Task.Delay(Timeout.InfiniteTimeSpan, requestToken).WithTestTimeout();
            await recorded.Should().ThrowAsync<OperationCanceledException>();
            cancellation.Cancel();
            second.SetCanceled(cancellation.Token);

            var act = () => run.WithTestTimeout();
            await act.Should().ThrowAsync<OperationCanceledException>();
            output.ToString().Should().NotContain("Scenario complete.");
        }
        finally
        {
            cancellation.Cancel();
            first.TrySetCanceled(cancellation.Token);
            second.TrySetCanceled(cancellation.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unexpected_drain_failure_cannot_replace_the_original_failure_or_user_shutdown(bool shutdown)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var client = CreateClient((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            if (Interlocked.Increment(ref requests) == 1)
                return first.Task;

            secondToken.TrySetResult(token);
            return second.Task;
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new TimerObservingTimeProvider();
        var original = new HttpRequestException("The initiating request failure.");
        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 30, 10), output, error, cancellation.Token, time);

        try
        {
            await time.AdvanceUntilAsync(secondToken.Task);
            var requestToken = await secondToken.Task;
            first.SetException(original);
            var stopping = () => Task.Delay(Timeout.InfiniteTimeSpan, requestToken)
                .WithTestTimeout();
            await stopping.Should().ThrowAsync<OperationCanceledException>();
            if (shutdown)
                cancellation.Cancel();
            second.SetException(new InvalidOperationException("An unexpected secondary failure."));

            var act = () => run.WithTestTimeout();
            if (shutdown)
            {
                await act.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                var exception = await act.Should().ThrowAsync<HttpRequestException>();
                exception.Which.Should().BeSameAs(original);
            }

            error.ToString().Should().Contain("Additional failure while stopping")
                .And.Contain("InvalidOperationException").And.Contain("unexpected secondary failure");
        }
        finally
        {
            cancellation.Cancel();
            first.TrySetCanceled(cancellation.Token);
            second.TrySetCanceled(cancellation.Token);
            await run.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Fact]
    public async Task A_failure_after_the_phase_window_cancels_other_in_flight_requests()
    {
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var client = CreateClient(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
                return new(HttpStatusCode.OK);

            if (Interlocked.Increment(ref requests) == 1)
                return await first.Task;

            secondStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(HttpStatusCode.OK);
            }
            finally
            {
                drained.TrySetResult();
            }
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new TimerObservingTimeProvider();
        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2), output, error, TestContext.Current.CancellationToken, time);

        await time.AdvanceUntilAsync(secondStarted.Task);
        time.Advance(TimeSpan.FromSeconds(1));
        first.SetException(new HttpRequestException("A request failed while draining."));

        var act = () => run.WithTestTimeout();
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*while draining*");
        requests.Should().Be(2);
        drained.Task.IsCompletedSuccessfully.Should().BeTrue();
        output.ToString().Should().NotContain("Scenario complete.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hanging_requests_apply_backpressure_at_the_documented_in_flight_limit(bool continuous)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requests = 0;
        var active = 0;
        var concurrency = new ConcurrentQueue<int>();
        using var client = CreateClient(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
                return new(HttpStatusCode.OK);

            Interlocked.Increment(ref requests);
            concurrency.Enqueue(Interlocked.Increment(ref active));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(HttpStatusCode.OK);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        using var output = new StringWriter();
        using var error = new BackpressureObservingWriter();
        var time = new TimerObservingTimeProvider();
        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 30, 1_000, Continuous: continuous),
            output, error, cancellation.Token, time);

        try
        {
            await time.AdvanceUntilAsync(error.LimitReached.Task);
            time.Advance(TimeSpan.FromSeconds(5));
            await TimerObservingTimeProvider.SettleAsync();

            Volatile.Read(ref requests).Should().Be(32);
            concurrency.Max().Should().Be(32);
            Volatile.Read(ref active).Should().Be(32);
            error.ToString().Should().Contain("In-flight limit (32) reached");
        }
        finally
        {
            cancellation.Cancel();
            var stop = () => run.WithTestTimeout();
            await stop.Should().ThrowAsync<OperationCanceledException>();
        }

        Volatile.Read(ref active).Should().Be(0);
    }

    [Fact]
    public async Task Resuming_after_backpressure_does_not_burst_to_catch_up_missed_requests()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requests = new ConcurrentQueue<TaskCompletionSource<HttpResponseMessage>>();
        using var client = CreateClient((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Enqueue(completion);
            return completion.Task.WaitAsync(token);
        });
        using var output = new StringWriter();
        using var error = new BackpressureObservingWriter();
        var time = new TimerObservingTimeProvider();
        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 30, 100, Continuous: true),
            output, error, cancellation.Token, time);

        try
        {
            await time.AdvanceUntilAsync(error.LimitReached.Task);
            time.Advance(TimeSpan.FromSeconds(5));
            await TimerObservingTimeProvider.SettleAsync();
            requests.Count.Should().Be(32);

            foreach (var completion in requests.ToArray())
                completion.SetResult(new(HttpStatusCode.OK));

            (await time.WaitForTimerAsync()).Should().Be(TimeSpan.FromMilliseconds(10));
            requests.Count.Should().Be(33);

            time.Advance(TimeSpan.FromMilliseconds(9));
            await TimerObservingTimeProvider.SettleAsync();
            requests.Count.Should().Be(33);

            time.Advance(TimeSpan.FromMilliseconds(1));
            (await time.WaitForTimerAsync()).Should().Be(TimeSpan.FromMilliseconds(10));
            requests.Count.Should().Be(34);
        }
        finally
        {
            cancellation.Cancel();
            var stop = () => run.WithTestTimeout();
            await stop.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Fact]
    public async Task Continuous_mode_does_not_treat_shutdown_as_a_request_timeout()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/health")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }

            return new(HttpStatusCode.OK);
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new FakeTimeProvider();

        var act = () => ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2, Continuous: true), output, error, cancellation.Token, time);

        await act.Should().ThrowAsync<OperationCanceledException>();
        output.ToString().Should().NotContain("timeouts=").And.NotContain("transport-failures=");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Request_pacing_accounts_for_synchronous_send_overhead()
    {
        var time = new FakeTimeProvider();
        var requests = 0;
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/health")
            {
                Interlocked.Increment(ref requests);
                time.Advance(TimeSpan.FromMilliseconds(100));
            }

            return new(HttpStatusCode.OK);
        });
        using var output = new StringWriter();
        using var error = new StringWriter();

        await ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 10), output, error, TestContext.Current.CancellationToken, time);

        requests.Should().Be(50, "each of five one-second phases should schedule ten requests");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Container_startup_waits_for_readiness_before_sending_traffic()
    {
        var healthChecks = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var client = CreateClient(request =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/health");
            return new(Interlocked.Increment(ref healthChecks) < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        });
        using var output = new CycleCancellingWriter(cancellation, "Cycle 1");
        using var error = new StringWriter();
        var time = new TimerObservingTimeProvider();

        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2, WaitForReady: true), output, error, cancellation.Token, time);
        (await time.WaitForTimerAsync()).Should().Be(TimeSpan.FromSeconds(30));
        await time.AdvanceUntilAsync(run);

        var act = () => run.WithTestTimeout();
        await act.Should().ThrowAsync<OperationCanceledException>();
        healthChecks.Should().Be(3);
        error.ToString().Should().Contain("Waiting for SliProductionDemo").And.Contain("503");
    }

    [Fact]
    public async Task Container_startup_retries_a_readiness_request_timeout()
    {
        var healthChecks = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var client = CreateClient(request =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/health");
            if (Interlocked.Increment(ref healthChecks) == 1)
                throw new TaskCanceledException("Simulated readiness timeout.");

            return new(HttpStatusCode.OK);
        });
        using var output = new CycleCancellingWriter(cancellation, "Cycle 1");
        using var error = new StringWriter();
        var time = new TimerObservingTimeProvider();

        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2, WaitForReady: true), output, error, cancellation.Token, time);
        (await time.WaitForTimerAsync()).Should().Be(TimeSpan.FromSeconds(30));
        await time.AdvanceUntilAsync(run);

        var act = () => run.WithTestTimeout();
        await act.Should().ThrowAsync<OperationCanceledException>();
        healthChecks.Should().Be(2);
        error.ToString().Should().Contain("GET /health timed out.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Container_startup_has_a_bounded_readiness_deadline(bool hanging)
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient((_, token) =>
        {
            requested.TrySetResult();
            return hanging
                ? pending.Task.WaitAsync(token)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new TimerObservingTimeProvider();
        var started = time.GetTimestamp();

        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2, WaitForReady: true),
            output, error, TestContext.Current.CancellationToken, time);
        (await time.WaitForTimerAsync()).Should().Be(TimeSpan.FromSeconds(30));

        // Only advance once the first readiness attempt is in flight (hanging) or has been logged and its
        // retry delay registered (failing), so the deadline cannot pre-empt the attempt being observed.
        await requested.Task.WithTestTimeout();
        if (!hanging)
            (await time.WaitForTimerAsync()).Should().Be(TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromMilliseconds(29_999));
        run.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromMilliseconds(1));

        var act = () => run.WithTestTimeout();
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*within 30 seconds*");
        time.GetElapsedTime(started).Should().Be(TimeSpan.FromSeconds(30));
        output.ToString().Should().BeEmpty();
        if (!hanging)
            error.ToString().Should().Contain("Waiting for SliProductionDemo");
    }

    [Fact]
    public async Task Unsuccessful_readiness_fails_without_counting_service_outcomes()
    {
        using var client = CreateClient(request =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/health");
            return new(HttpStatusCode.ServiceUnavailable);
        });
        using var output = new StringWriter();
        using var error = new StringWriter();

        var act = () => ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2), output, error, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*503*");
        output.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("transport", "an HTTP request")]
    [InlineData("timeout", "timed out")]
    [InlineData("unexpected", "InvalidOperationException")]
    public async Task Console_failures_are_reported_without_an_unhandled_stack_trace(string kind, string diagnostic)
    {
        Exception failure = kind switch
        {
            "transport" => new HttpRequestException("Simulated transport failure."),
            "timeout" => new TaskCanceledException("Simulated request timeout."),
            _ => new InvalidOperationException("Simulated unexpected request failure."),
        };
        using var client = CreateClient(request =>
            request.RequestUri!.AbsolutePath == "/health" ? new(HttpStatusCode.OK) : throw failure);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new FakeTimeProvider();
        var run = ScenarioRunner.RunConsoleAsync(
            client, new(client.BaseAddress!, 30, 10), output, error, TestContext.Current.CancellationToken, time);

        await run.WithTestTimeout();

        (await run).Should().Be(1);
        error.ToString().Should().Contain("Scenario stopped:").And.Contain(diagnostic)
            .And.NotContain("Unhandled exception").And.NotContain("   at ");
        output.ToString().Should().NotContain("Scenario complete.");
    }

    [Fact]
    public async Task Console_shutdown_returns_zero_without_reporting_a_timeout()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        using var client = CreateClient(_ => new(HttpStatusCode.OK));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ScenarioRunner.RunConsoleAsync(
            client, new(client.BaseAddress!, 30, 10), output, error, cancellation.Token);

        exitCode.Should().Be(0);
        output.ToString().Should().Contain("Scenario stopped.");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Only_the_first_stop_signal_suppresses_process_termination()
    {
        using var cancellation = new CancellationTokenSource();
        using var error = new StringWriter();
        var shutdown = new GeneratorShutdown(cancellation, error);
        var suppression = new List<bool>();

        shutdown.RequestCancellation(suppression.Add);
        cancellation.IsCancellationRequested.Should().BeTrue();
        shutdown.RequestCancellation(suppression.Add);
        shutdown.RequestCancellation(suppression.Add);

        suppression.Should().Equal(true, false, false);
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_stop_signals_only_suppress_one_termination()
    {
        using var cancellation = new CancellationTokenSource();
        using var error = new StringWriter();
        var shutdown = new GeneratorShutdown(cancellation, error);
        var results = new ConcurrentBag<bool>();

        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => shutdown.RequestCancellation(results.Add), TestContext.Current.CancellationToken)));

        results.Count(suppressed => suppressed).Should().Be(1);
        cancellation.IsCancellationRequested.Should().BeTrue();
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Signal_suppression_is_set_before_cancellation_callbacks_run()
    {
        using var cancellation = new CancellationTokenSource();
        using var error = new StringWriter();
        var shutdown = new GeneratorShutdown(cancellation, error);
        var suppressed = false;
        bool? observedSuppression = null;
        using var registration = cancellation.Token.Register(() => observedSuppression = suppressed);

        shutdown.RequestCancellation(value => suppressed = value);

        observedSuppression.Should().BeTrue();
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Cancellation_callback_failures_are_reported_and_do_not_suppress_a_second_signal()
    {
        using var cancellation = new CancellationTokenSource();
        using var error = new StringWriter();
        var shutdown = new GeneratorShutdown(cancellation, error);
        var suppressed = false;
        using var registration = cancellation.Token.Register(() => throw new InvalidOperationException("Broken callback."));

        var first = () => shutdown.RequestCancellation(value => suppressed = value);
        first.Should().NotThrow();
        suppressed.Should().BeTrue();
        cancellation.IsCancellationRequested.Should().BeTrue();
        error.ToString().Should().Contain("Shutdown cancellation callback failed:")
            .And.Contain("InvalidOperationException").And.Contain("Broken callback.");

        shutdown.RequestCancellation(value => suppressed = value);

        suppressed.Should().BeFalse();
        error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
    }

    [Fact]
    public async Task A_second_stop_signal_does_not_wait_for_stalled_request_draining()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient((request, _) =>
            request.RequestUri!.AbsolutePath == "/health"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
                : completion.Task);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var shutdown = new GeneratorShutdown(cancellation, error);
        var suppressed = false;
        var run = ScenarioRunner.RunConsoleAsync(
            client, new(client.BaseAddress!, 30, 10), output, error, cancellation.Token, new FakeTimeProvider());

        try
        {
            shutdown.RequestCancellation(value => suppressed = value);
            suppressed.Should().BeTrue();
            run.IsCompleted.Should().BeFalse("the pending handler deliberately ignores cancellation");
            shutdown.RequestCancellation(value => suppressed = value);
            suppressed.Should().BeFalse("another signal must permit immediate process termination");
        }
        finally
        {
            completion.TrySetCanceled(cancellation.Token);
        }

        (await run.WithTestTimeout()).Should().Be(0);
        error.ToString().Should().BeEmpty();
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        CreateClient((request, _) => Task.FromResult(respond(request)));

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new ResponseHandler(respond)) { BaseAddress = new Uri("http://demo.test/") };

    private sealed class TimerObservingTimeProvider : FakeTimeProvider
    {
        private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);

        private readonly Channel<TrackedTimer> timers = Channel.CreateUnbounded<TrackedTimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TrackedTimer(base.CreateTimer(callback, state, dueTime, period), GetUtcNow() + dueTime);
            timers.Writer.TryWrite(timer).Should().BeTrue();
            return timer;
        }

        /// <summary>
        /// Waits for the next timer that is still live and returns how long remains until it is due.
        /// Timers that were disposed or cancelled before they were observed are skipped.
        /// </summary>
        public async Task<TimeSpan> WaitForTimerAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TestTimeout.Default);
            while (true)
            {
                ThrowIfExpired(timeout);
                if (TryTakeLiveTimer(out var remaining))
                    return remaining;

                await timers.Reader.WaitToReadAsync(timeout.Token);
            }
        }

        /// <summary>
        /// Advances virtual time to each live timer as it is registered until <paramref name="goal"/> completes,
        /// without assuming how many timers the code under test creates.
        /// </summary>
        public async Task AdvanceUntilAsync(Task goal, TimeSpan? limit = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(limit ?? TestTimeout.Default);
            while (!goal.IsCompleted)
            {
                // Timer callbacks can register the next timer synchronously inside Advance, so this
                // loop may never reach the await below; check the deadline on every iteration.
                ThrowIfExpired(timeout);
                if (TryTakeLiveTimer(out var remaining))
                {
                    Advance(remaining);
                    continue;
                }

                var registered = timers.Reader.WaitToReadAsync(timeout.Token).AsTask();
                await Task.WhenAny(goal, registered);
                if (!goal.IsCompleted && registered.IsCanceled)
                    ThrowIfExpired(timeout);
            }
        }

        private static void ThrowIfExpired(CancellationTokenSource timeout)
        {
            if (!timeout.IsCancellationRequested)
                return;

            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The scenario did not reach the goal before the test timeout.");
        }

        /// <summary>
        /// Gives the code under test real time to react, so that "nothing else happened" assertions
        /// are not evaluated before any continuation could have run.
        /// </summary>
        public static Task SettleAsync() => Task.Delay(Grace, TestContext.Current.CancellationToken);

        private bool TryTakeLiveTimer(out TimeSpan remaining)
        {
            while (timers.Reader.TryRead(out var timer))
            {
                if (timer.Disposed)
                    continue;

                remaining = timer.Due - GetUtcNow();
                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;
                return true;
            }

            remaining = default;
            return false;
        }

        private sealed class TrackedTimer(ITimer inner, DateTimeOffset due) : ITimer
        {
            private volatile bool disposed;

            public DateTimeOffset Due => due;

            public bool Disposed => disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                disposed = true;
                inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                disposed = true;
                return inner.DisposeAsync();
            }
        }
    }

    private sealed class ResponseHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request, cancellationToken);
        }
    }

    private sealed class PayloadObservingClient(HttpMessageHandler handler) : HttpClient(handler)
    {
        public Task<HttpResponseMessage>? FirstPayload { get; private set; }

        public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var send = base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath != "/health")
                FirstPayload ??= send;
            return send;
        }
    }

    private sealed class BackpressureObservingWriter : StringWriter
    {
        public TaskCompletionSource LimitReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith("In-flight limit", StringComparison.Ordinal) == true)
                LimitReached.TrySetResult();
        }
    }

    private sealed class CycleCancellingWriter(CancellationTokenSource cancellation, string stopAt) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value == stopAt)
                cancellation.Cancel();
        }
    }
}
