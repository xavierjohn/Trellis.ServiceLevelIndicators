namespace GenerateSli.Tests;

using System.Net;
using Microsoft.Extensions.Time.Testing;

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
    public async Task Finite_mode_completes_exactly_one_cycle()
    {
        using var client = CreateClient(_ => new(HttpStatusCode.OK));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new FakeTimeProvider();

        await AdvanceUntilCompleteAsync(
            ScenarioRunner.RunAsync(
                client, new(client.BaseAddress!, 1, 2), output, error, TestContext.Current.CancellationToken, time),
            time);

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
        var time = new FakeTimeProvider();

        var act = () => AdvanceUntilCompleteAsync(
            ScenarioRunner.RunAsync(
                client, new(client.BaseAddress!, 1, 2, Continuous: true), output, error, cancellation.Token, time),
            time);

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
            if (request.RequestUri!.AbsolutePath != "/health" && ++requests == 1)
            {
                if (timeout)
                    throw new TaskCanceledException("Simulated request timeout.");

                throw new HttpRequestException("Simulated API restart.");
            }

            return new(HttpStatusCode.OK);
        });
        using var output = new CycleCancellingWriter(cancellation, "Cycle 2");
        using var error = new StringWriter();
        var time = new FakeTimeProvider();

        var run = ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2, Continuous: true), output, error, cancellation.Token, time);

        run.IsCompleted.Should().BeFalse();
        error.ToString().Should().Contain(diagnostic).And.Contain("Continuing traffic.");

        var act = () => AdvanceUntilCompleteAsync(run, time);
        await act.Should().ThrowAsync<OperationCanceledException>();

        requests.Should().BeGreaterThan(1);
        output.ToString().Should().Contain($"{countName}=1").And.Contain("200=").And.Contain("Cycle 2");
        output.ToString().Should().NotContain(timeout ? "transport-failures=" : "timeouts=");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finite_mode_still_propagates_transport_failures_and_timeouts(bool timeout)
    {
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
                return new(HttpStatusCode.OK);

            if (timeout)
                throw new TaskCanceledException("Simulated request timeout.");

            throw new HttpRequestException("Simulated API restart.");
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new FakeTimeProvider();

        var act = () => AdvanceUntilCompleteAsync(
            ScenarioRunner.RunAsync(
                client, new(client.BaseAddress!, 1, 2), output, error, TestContext.Current.CancellationToken, time),
            time);

        await act.Should().ThrowAsync<Exception>()
            .Where(ex => timeout ? ex is OperationCanceledException : ex is HttpRequestException);
        output.ToString().Should().NotContain("Scenario complete.");
        error.ToString().Should().BeEmpty();
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
                requests++;
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
            return new(++healthChecks < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        });
        using var output = new CycleCancellingWriter(cancellation, "Cycle 1");
        using var error = new StringWriter();
        var time = new FakeTimeProvider();

        var act = () => AdvanceUntilCompleteAsync(
            ScenarioRunner.RunAsync(
                client, new(client.BaseAddress!, 1, 2, WaitForReady: true), output, error, cancellation.Token, time),
            time);

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
            if (++healthChecks == 1)
                throw new TaskCanceledException("Simulated readiness timeout.");

            return new(HttpStatusCode.OK);
        });
        using var output = new CycleCancellingWriter(cancellation, "Cycle 1");
        using var error = new StringWriter();
        var time = new FakeTimeProvider();

        var act = () => AdvanceUntilCompleteAsync(
            ScenarioRunner.RunAsync(
                client, new(client.BaseAddress!, 1, 2, WaitForReady: true), output, error, cancellation.Token, time),
            time);

        await act.Should().ThrowAsync<OperationCanceledException>();
        healthChecks.Should().Be(2);
        error.ToString().Should().Contain("GET /health timed out.");
    }

    [Fact]
    public async Task Container_startup_has_a_bounded_readiness_deadline()
    {
        using var client = CreateClient(_ => new(HttpStatusCode.ServiceUnavailable));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var time = new FakeTimeProvider();

        var act = () => AdvanceUntilCompleteAsync(
            ScenarioRunner.RunAsync(
                client, new(client.BaseAddress!, 1, 2, WaitForReady: true),
                output, error, TestContext.Current.CancellationToken, time),
            time);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*within 30 seconds*");
        output.ToString().Should().BeEmpty();
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

    private static async Task AdvanceUntilCompleteAsync(Task run, FakeTimeProvider time)
    {
        for (var i = 0; i < 6_000 && !run.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Yield();
        }

        run.IsCompleted.Should().BeTrue("the scenario should finish within one virtual minute");
        await run;
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new ResponseHandler(respond)) { BaseAddress = new Uri("http://demo.test/") };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
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
