namespace GenerateSli.Tests;

using System.Net;

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

        await ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2), output, error, TestContext.Current.CancellationToken);

        output.ToString().Should().Contain("Cycle 1").And.NotContain("Cycle 2").And.Contain("Scenario complete.");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Continuous_mode_starts_another_cycle_and_honors_cancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = CreateClient(_ => new(HttpStatusCode.OK));
        using var output = new CycleCancellingWriter(cancellation, "Cycle 2");
        using var error = new StringWriter();

        var act = () => ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2, Continuous: true), output, error, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        output.ToString().Should().Contain("Cycle 1").And.Contain("Cycle 2").And.NotContain("Scenario complete.");
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

        var act = () => ScenarioRunner.RunAsync(
            client, new(client.BaseAddress!, 1, 2, WaitForReady: true), output, error, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        healthChecks.Should().Be(3);
        error.ToString().Should().Contain("Waiting for SliProductionDemo").And.Contain("503");
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
